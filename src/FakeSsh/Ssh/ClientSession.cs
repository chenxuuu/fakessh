using System.Text;
using FakeSsh.AI;
using FakeSsh.Config;
using FakeSsh.FileSystem;
using FakeSsh.Logging;
using FakeSsh.Terminal;
using FxSsh;
using FxSsh.Services;

namespace FakeSsh.Ssh;

/// <summary>
/// Handles a single SSH client session: authentication, shell, AI integration
/// </summary>
public class ClientSession
{
    private readonly Session _session;
    private readonly AppConfig _config;
    private readonly ChatService _chatService;
    private readonly LogStore _logStore;
    private readonly ILogger _logger;

    private SessionChannel? _channel;
    private LineEditor? _lineEditor;
    private VirtualFileSystem? _vfs;

    // Session state
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private string _username = "";
    private string _clientIp = "";
    private string _cwd = "/root";
    private string _terminalType = "xterm-256color";
    private uint _termWidth = 80;
    private uint _termHeight = 24;
    private DateTime _loginTime;
    private readonly DateTime _sessionStart = DateTime.UtcNow;
    private bool _disconnected;
    private CancellationTokenSource _cts = new();

    // AI context
    private readonly List<AiMessage> _conversationHistory = new();
    private readonly List<(string cmd, string output)> _commandLog = new();

    // For streaming output - we must not accept input while outputting
    private readonly SemaphoreSlim _outputLock = new(1, 1);

    // Commands that produce line-by-line output (for realistic streaming)
    private static readonly HashSet<string> SlowCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "apt", "apt-get", "dpkg", "wget", "curl", "pip", "pip3",
        "make", "gcc", "g++", "cc", "cmake",
        "find", "grep", "locate", "updatedb",
        "tar", "gzip", "bzip2", "xz", "unzip",
        "scp", "rsync", "git"
    };

    // Commands that use interactive TUI (not supported)
    private static readonly HashSet<string> InteractiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "vi", "vim", "nvim", "nano", "emacs", "pico", "joe", "ed",
        "top", "htop", "btop", "atop", "nmon", "glances",
        "less", "more", "most",
        "tmux", "screen", "byobu",
        "mc", "ranger",
        "cfdisk", "fdisk", "parted",
        "alsamixer", "ncdu", "nnn",
        "watch", "dialog", "whiptail",
        "mutt", "alpine", "irssi", "weechat"
    };

    public ClientSession(
        Session session,
        AppConfig config,
        ChatService chatService,
        LogStore logStore,
        ILogger logger,
        string? clientIp = null)
    {
        _session = session;
        _config = config;
        _chatService = chatService;
        _logStore = logStore;
        _logger = logger;

        // Try to extract client IP from session
        _clientIp = clientIp ?? "unknown";

        _logStore.CreateSession(_sessionId, _clientIp);
    }

    public void Start()
    {
        _session.ServiceRegistered += OnServiceRegistered;
        _session.Disconnected += OnDisconnected;
    }

    private void OnServiceRegistered(object? sender, SshService service)
    {
        if (service is UserauthService authService)
        {
            authService.Userauth += OnAuth;
        }
        else if (service is ConnectionService connService)
        {
            connService.PtyReceived += OnPtyReceived;
            connService.CommandOpened += OnCommandOpened;
            connService.WindowChange += OnWindowChange;
        }
    }

    private void OnAuth(object? sender, UserauthArgs args)
    {
        if (args.AuthMethod == "password")
        {
            var user = _config.Users.FirstOrDefault(u =>
                u.Username == args.Username && u.Password == args.Password);

            args.Result = user != null;
            _logStore.LogAuth(_sessionId, _clientIp, args.Username, args.Result);

            if (args.Result)
            {
                _username = args.Username;
                _cwd = _username == "root" ? "/root" : $"/home/{_username}";
                _loginTime = DateTime.UtcNow;
            }
        }
        else if (args.AuthMethod == "publickey")
        {
            // Reject all public key auth
            args.Result = false;
        }
    }

    private void OnPtyReceived(object? sender, PtyArgs args)
    {
        _terminalType = args.Terminal ?? "xterm-256color";
        _termWidth = args.WidthChars > 0 ? args.WidthChars : 80;
        _termHeight = args.HeightRows > 0 ? args.HeightRows : 24;
    }

    private void OnWindowChange(object? sender, WindowChangeArgs args)
    {
        _termWidth = args.WidthColumns > 0 ? args.WidthColumns : _termWidth;
        _termHeight = args.HeightRows > 0 ? args.HeightRows : _termHeight;
    }

    private void OnCommandOpened(object? sender, CommandRequestedArgs args)
    {
        if (args.ShellType == "shell")
        {
            _channel = args.Channel;
            _vfs = new VirtualFileSystem(_username);

            _lineEditor = new LineEditor(
                send: SendToClient,
                onCommand: OnCommandEntered,
                getPrompt: GetPrompt,
                maxInputRate: _config.MaxInputRatePerSecond);

            _channel.DataReceived += OnDataReceived;
            _channel.CloseReceived += OnChannelClosed;

            // Send MOTD and first prompt
            Task.Run(() => SendMotdAndPrompt());
        }
        else if (args.ShellType == "exec")
        {
            // Handle single command execution
            _channel = args.Channel;
            _vfs = new VirtualFileSystem(_username);

            Task.Run(async () =>
            {
                try
                {
                    var command = args.CommandText ?? "";
                    _logStore.LogCommand(_sessionId, _username, command);
                    var output = await ExecuteCommandForOutput(command);
                    SendText(output);
                    _channel.SendEof();
                    _channel.SendClose(0);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing command");
                    _channel.SendClose(1);
                }
            });
        }
    }

    private async void OnDataReceived(object? sender, byte[] data)
    {
        if (_disconnected || _lineEditor == null) return;

        try
        {
            await _lineEditor.ProcessBytes(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing input in session {Session}", _sessionId[..8]);
        }
    }

    private void OnChannelClosed(object? sender, EventArgs e)
    {
        HandleDisconnect();
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        HandleDisconnect();
    }

    private void HandleDisconnect()
    {
        if (_disconnected) return;
        _disconnected = true;
        _cts.Cancel();
        _logStore.LogDisconnect(_sessionId);
    }

    private async Task OnCommandEntered(string rawCommand)
    {
        if (_disconnected) return;

        var command = rawCommand.Trim();
        if (string.IsNullOrEmpty(command))
        {
            _lineEditor?.SendPrompt();
            return;
        }

        _logStore.LogCommand(_sessionId, _username, command);

        try
        {
            await _outputLock.WaitAsync(_cts.Token);
            try
            {
                await ProcessCommand(command);
            }
            finally
            {
                _outputLock.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing command: {Command}", command);
            SendText($"bash: internal error\r\n");
        }

        if (!_disconnected)
        {
            _lineEditor?.SendPrompt();
        }
    }

    private async Task ProcessCommand(string command)
    {
        // Parse first word
        var firstWord = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // Remove any sudo prefix for command detection
        var effectiveCmd = firstWord;
        var effectiveParts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (effectiveCmd == "sudo" && effectiveParts.Length > 1)
        {
            effectiveCmd = effectiveParts[1];
        }

        // Handle interactive commands (not supported)
        if (InteractiveCommands.Contains(effectiveCmd))
        {
            SendText($"bash: {effectiveCmd}: command not found\r\n");
            _commandLog.Add((command, $"bash: {effectiveCmd}: command not found"));
            return;
        }

        // Handle local commands
        switch (effectiveCmd.ToLower())
        {
            case "cd":
                HandleCd(command);
                return;
            case "pwd":
                SendText($"{_cwd}\r\n");
                _commandLog.Add((command, _cwd));
                return;
            case "exit" or "logout":
                SendText("logout\r\n");
                _channel?.SendEof();
                _channel?.SendClose(0);
                HandleDisconnect();
                return;
            case "clear":
                SendText("\x1b[2J\x1b[H");
                _commandLog.Add((command, "(screen cleared)"));
                return;
            case "history":
                var histOutput = new StringBuilder();
                for (int i = 0; i < _commandLog.Count; i++)
                {
                    histOutput.Append($"  {i + 1,4}  {_commandLog[i].cmd}\r\n");
                }
                SendText(histOutput.ToString());
                _commandLog.Add((command, histOutput.ToString()));
                return;
        }

        // Apply filesystem side effects
        _vfs?.ApplyCommandSideEffects(command, _cwd);

        // Determine display mode
        bool isSlowCommand = SlowCommands.Contains(effectiveCmd);

        // Send to AI
        await SendToAi(command, isSlowCommand);
    }

    private void HandleCd(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string target;

        if (parts.Length < 2 || parts[1] == "~")
        {
            target = _username == "root" ? "/root" : $"/home/{_username}";
        }
        else if (parts[1] == "-")
        {
            target = _cwd; // simplified: just stay
        }
        else
        {
            target = _vfs?.ResolvePath(_cwd, parts[1]) ?? _cwd;
        }

        if (_vfs?.DirectoryExists(target) == true)
        {
            _cwd = target;
            _commandLog.Add((command, ""));
        }
        else
        {
            var msg = $"bash: cd: {parts.ElementAtOrDefault(1) ?? ""}: No such file or directory";
            SendText(msg + "\r\n");
            _commandLog.Add((command, msg));
        }
    }

    private async Task SendToAi(string command, bool isSlowCommand)
    {
        try
        {
            var systemPrompt = SystemPromptBuilder.Build(
                hostname: _config.Hostname,
                username: _username,
                cwd: _cwd,
                clientIp: _clientIp,
                terminal: _terminalType,
                termWidth: _termWidth,
                termHeight: _termHeight,
                loginTime: _loginTime,
                sessionStart: _sessionStart,
                fsChanges: _vfs?.GetChangesSummary() ?? "",
                userFiles: _vfs?.GetUserFilesInfo() ?? "",
                recentCommands: _commandLog);

            var fullResponse = new StringBuilder();
            _lineBuffer.Clear();
            _currentOutputBytes = 0;
            _currentOutputLines = 0;
            _consecutiveNumberedLines = 0;

            // Determine delay profile based on command type
            int lineDelayMin = isSlowCommand ? 30 : 5;
            int lineDelayMax = isSlowCommand ? 200 : 50;

            int chunkCount = 0;
            await foreach (var chunk in _chatService.StreamChatAsync(
                systemPrompt, command, _conversationHistory, _cts.Token))
            {
                chunkCount++;
                fullResponse.Append(chunk);
                // All output uses line-by-line streaming
                await StreamLineByLine(chunk, lineDelayMin, lineDelayMax);
            }

            // Flush remaining buffer content
            await FlushLineBuffer(lineDelayMin, lineDelayMax);

            var response = fullResponse.ToString();

            _logger.LogDebug("[{Session}] AI returned {Chunks} chunks, {Len} chars for: {Cmd}",
                _sessionId[..8], chunkCount, response.Length, command);

            // Log warning if AI returned nothing for a command that should have output
            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("[{Session}] AI returned EMPTY response for command: {Cmd}",
                    _sessionId[..8], command);
            }

            // Check for empty output convention
            if (IsEmptyResponse(response))
            {
                response = "";
            }
            else
            {
                // Ensure response ends with newline
                if (!string.IsNullOrEmpty(response) && !response.EndsWith('\n'))
                {
                    SendText("\r\n");
                }
            }

            // Add to conversation history
            _conversationHistory.Add(new AiMessage { Role = "user", Content = command });
            _conversationHistory.Add(new AiMessage { Role = "assistant", Content = response });

            // Trim conversation history to avoid token overflow
            while (_conversationHistory.Count > 40)
            {
                _conversationHistory.RemoveAt(0);
            }

            _commandLog.Add((command, response));
            _logStore.LogAiResponse(_sessionId, _username, command, response);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI service error for command: {Command}", command);
            SendText($"bash: {command.Split(' ')[0]}: command not found\r\n");
        }
    }

    // Buffer for line-by-line streaming
    private readonly StringBuilder _lineBuffer = new();

    // Max chars to send per chunk when a line is very long
    private const int MaxLineChunkSize = 120;

    // Track total output bytes sent for current command
    private int _currentOutputBytes;
    private int _currentOutputLines;

    // Pattern detection for numbered list loops
    private int _consecutiveNumberedLines;
    private static readonly System.Text.RegularExpressions.Regex NumberedLinePattern =
        new(@"^\s*\d{1,6}[\.\)\]:：、]\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    private async Task StreamLineByLine(string chunk, int delayMin, int delayMax)
    {
        _lineBuffer.Append(chunk);

        while (true)
        {
            // Check output limits
            if (_currentOutputBytes >= _config.MaxOutputLength || _currentOutputLines >= 200)
            {
                _lineBuffer.Clear();
                return;
            }

            var content = _lineBuffer.ToString();
            var newlineIdx = content.IndexOf('\n');

            if (newlineIdx < 0)
            {
                // No complete line yet. If buffer is very long, send partial
                if (_lineBuffer.Length > MaxLineChunkSize)
                {
                    var partial = _lineBuffer.ToString(0, MaxLineChunkSize);
                    _lineBuffer.Remove(0, MaxLineChunkSize);
                    SendText(partial);
                    _currentOutputBytes += Encoding.UTF8.GetByteCount(partial);
                    await RandomDelay(delayMin, delayMax);
                }
                break;
            }

            var line = content[..(newlineIdx + 1)];
            _lineBuffer.Remove(0, newlineIdx + 1);

            // Only skip the exact convention marker, NOT regular empty lines
            var lineTrimmed = line.Trim();
            if (IsNoOutputMarker(lineTrimmed))
                continue;

            // Detect numbered list loop pattern (e.g. "1. xxx\n2. xxx\n3. xxx...")
            if (NumberedLinePattern.IsMatch(lineTrimmed))
            {
                _consecutiveNumberedLines++;
                if (_consecutiveNumberedLines > 15)
                {
                    // Kill the rest — this is a runaway numbered list
                    _logger.LogWarning("[{Session}] Killed numbered list loop at line {N}",
                        _sessionId[..8], _consecutiveNumberedLines);
                    _lineBuffer.Clear();
                    _currentOutputLines = 200; // force stop
                    return;
                }
            }
            else
            {
                _consecutiveNumberedLines = 0;
            }

            var sanitizedLine = line.Replace("\n", "\r\n");

            // If line is very long, split it
            if (sanitizedLine.Length > MaxLineChunkSize)
            {
                for (int i = 0; i < sanitizedLine.Length; i += MaxLineChunkSize)
                {
                    var end = Math.Min(i + MaxLineChunkSize, sanitizedLine.Length);
                    var part = sanitizedLine[i..end];
                    SendText(part);
                    _currentOutputBytes += Encoding.UTF8.GetByteCount(part);
                    await RandomDelay(delayMin / 2, delayMax / 2);
                }
            }
            else
            {
                SendText(sanitizedLine);
                _currentOutputBytes += Encoding.UTF8.GetByteCount(sanitizedLine);
            }

            _currentOutputLines++;
            await RandomDelay(delayMin, delayMax);
        }
    }

    private async Task FlushLineBuffer(int delayMin, int delayMax)
    {
        if (_lineBuffer.Length > 0)
        {
            var remaining = _lineBuffer.ToString();
            _lineBuffer.Clear();

            if (!IsNoOutputMarker(remaining.Trim()))
            {
                var sanitized = remaining.Replace("\n", "\r\n");
                SendText(sanitized);
                await RandomDelay(delayMin, delayMax);
            }
        }
    }

    /// <summary>
    /// Check if AI response is the empty output convention marker.
    /// Only matches explicit convention strings, NOT regular empty lines.
    /// </summary>
    private static bool IsNoOutputMarker(string text)
    {
        var trimmed = text.Trim();
        return trimmed is "<EMPTY>" or "<NO_OUTPUT>" or "(empty)"
            or "(no output)" or "(无输出)";
    }

    /// <summary>
    /// Check if the complete AI response represents "no output"
    /// </summary>
    private static bool IsEmptyResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return true;
        return IsNoOutputMarker(response);
    }

    private async Task RandomDelay(int minMs, int maxMs)
    {
        // Add occasional larger spikes
        var delay = Random.Shared.Next(minMs, maxMs);
        if (Random.Shared.NextDouble() < 0.1) // 10% chance of spike
        {
            delay += Random.Shared.Next(100, 500);
        }
        await Task.Delay(delay, _cts.Token);
    }

    private async Task<string> ExecuteCommandForOutput(string command)
    {
        var systemPrompt = SystemPromptBuilder.Build(
            hostname: _config.Hostname,
            username: _username,
            cwd: _cwd,
            clientIp: _clientIp,
            terminal: _terminalType,
            termWidth: _termWidth,
            termHeight: _termHeight,
            loginTime: _loginTime,
            sessionStart: _sessionStart,
            fsChanges: _vfs?.GetChangesSummary() ?? "",
            userFiles: _vfs?.GetUserFilesInfo() ?? "",
            recentCommands: _commandLog);

        return await _chatService.CompleteChatAsync(
            systemPrompt, command, _conversationHistory, _cts.Token);
    }

    private async Task SendMotdAndPrompt()
    {
        // Simulate realistic SSH login delay
        await Task.Delay(Random.Shared.Next(100, 300));

        var motd = BuildMotd();
        SendText(motd);

        await Task.Delay(Random.Shared.Next(50, 150));
        _lineEditor?.SendPrompt();
    }

    private string BuildMotd()
    {
        var lastLogin = DateTime.UtcNow.AddHours(-Random.Shared.Next(1, 72));
        var lastIp = $"192.168.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";

        return $"Linux {_config.Hostname} 6.1.0-18-amd64 #1 SMP PREEMPT_DYNAMIC Debian 6.1.76-1 (2024-02-01) x86_64\r\n" +
               $"\r\n" +
               $"The programs included with the Debian GNU/Linux system are free software;\r\n" +
               $"the exact distribution terms for each program are described in the\r\n" +
               $"individual files in /usr/share/doc/*/copyright.\r\n" +
               $"\r\n" +
               $"Debian GNU/Linux comes with ABSOLUTELY NO WARRANTY, to the extent\r\n" +
               $"permitted by applicable law.\r\n" +
               $"Last login: {lastLogin:ddd MMM dd HH:mm:ss yyyy} from {lastIp}\r\n";
    }

    private string GetPrompt()
    {
        var cwdDisplay = _cwd;
        var home = _username == "root" ? "/root" : $"/home/{_username}";

        if (cwdDisplay == home)
        {
            cwdDisplay = "~";
        }
        else if (cwdDisplay.StartsWith(home + "/"))
        {
            cwdDisplay = "~" + cwdDisplay[home.Length..];
        }

        var symbol = _username == "root" ? "#" : "$";
        return $"{_username}@{_config.Hostname}:{cwdDisplay}{symbol} ";
    }

    private void SendText(string text)
    {
        if (_disconnected || _channel == null) return;
        try
        {
            _channel.SendData(Encoding.UTF8.GetBytes(text));
        }
        catch
        {
            // Channel may be closed
        }
    }

    private void SendToClient(byte[] data)
    {
        if (_disconnected || _channel == null) return;
        try
        {
            _channel.SendData(data);
        }
        catch
        {
            // Channel may be closed
        }
    }
}

using System.Net;
using System.Reflection;
using FakeSsh.AI;
using FakeSsh.Config;
using FakeSsh.Logging;
using FxSsh;
using FxSsh.Services;
using Microsoft.Extensions.Options;

namespace FakeSsh.Ssh;

/// <summary>
/// Background service that hosts the SSH server
/// </summary>
public class SshServerHost : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<SshServerHost> _logger;
    private SshServer? _server;

    public SshServerHost(
        IOptions<AppConfig> config,
        IServiceProvider services,
        ILogger<SshServerHost> logger)
    {
        _config = config.Value;
        _services = services;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hostKey = LoadOrGenerateHostKey();

        // Use a banner that looks like real OpenSSH on Debian
        var info = new StartingInfo(
            IPAddress.Any,
            _config.SshPort,
            "SSH-2.0-OpenSSH_9.2p1 Debian-2+deb12u3");

        _server = new SshServer(info);
        _server.AddHostKey("rsa-sha2-256", hostKey);
        _server.AddHostKey("rsa-sha2-512", hostKey);

        _server.ConnectionAccepted += OnConnectionAccepted;
        _server.ExceptionRasied += (s, e) =>
        {
            // Socket timeouts and disconnections are normal in honeypot scenarios
            var msg = e.Message ?? "";
            if (msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("ConnectionLost", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("connection was reset", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
                e is System.IO.IOException ||
                e is System.Net.Sockets.SocketException)
            {
                _logger.LogDebug("SSH connection event: {Message}", msg);
            }
            else
            {
                _logger.LogError(e, "SSH server error: {Message}", msg);
            }
        };

        _server.Start();
        _logger.LogInformation("SSH server listening on port {Port}", _config.SshPort);

        stoppingToken.Register(() =>
        {
            _logger.LogInformation("Stopping SSH server...");
            _server?.Stop();
        });

        return Task.CompletedTask;
    }

    private void OnConnectionAccepted(object? sender, Session session)
    {
        _logger.LogInformation("New SSH connection: {Client}", session.ClientVersion);

        // Override the hardcoded 30s timeout via reflection
        try
        {
            var timeoutField = typeof(Session).GetField("_timeout", BindingFlags.NonPublic | BindingFlags.Instance);
            if (timeoutField != null)
            {
                timeoutField.SetValue(session, TimeSpan.FromSeconds(_config.SessionTimeoutSeconds));
                _logger.LogDebug("Session timeout set to {Seconds}s", _config.SessionTimeoutSeconds);
            }
            else
            {
                _logger.LogWarning("Could not find Session._timeout field for reflection override");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to override session timeout via reflection");
        }

        // Extract client IP from underlying socket via reflection
        string? clientIp = null;
        try
        {
            var socketField = typeof(Session).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);
            if (socketField?.GetValue(session) is System.Net.Sockets.Socket socket &&
                socket.RemoteEndPoint is System.Net.IPEndPoint remoteEp)
            {
                clientIp = remoteEp.Address.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract client IP via reflection");
        }

        try
        {
            var chatService = _services.GetRequiredService<ChatService>();
            var logStore = _services.GetRequiredService<LogStore>();
            var clientLogger = _services.GetRequiredService<ILoggerFactory>().CreateLogger<ClientSession>();

            var clientSession = new ClientSession(session, _config, chatService, logStore, clientLogger, clientIp);
            clientSession.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize client session: {Message}", ex.Message);
        }
    }

    private string LoadOrGenerateHostKey()
    {
        var keyPath = _config.HostKeyPath;

        if (File.Exists(keyPath))
        {
            _logger.LogInformation("Loading host key from {Path}", keyPath);
            return File.ReadAllText(keyPath);
        }

        _logger.LogInformation("Generating new RSA 4096-bit host key -> {Path}", keyPath);
        var key = KeyGenerator.GenerateRsaKeyPem(4096);
        File.WriteAllText(keyPath, key);
        return key;
    }

    public override void Dispose()
    {
        _server?.Dispose();
        base.Dispose();
    }
}

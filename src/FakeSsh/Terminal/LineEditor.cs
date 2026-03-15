using System.Text;

namespace FakeSsh.Terminal;

/// <summary>
/// Terminal line editor that handles raw byte input from SSH channel,
/// processes escape sequences, and provides line editing features.
/// </summary>
public class LineEditor
{
    private readonly StringBuilder _buffer = new();
    private int _cursorPos;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private string _savedLine = "";

    // Escape sequence state machine
    private enum State { Normal, Escape, CSI }
    private State _state = State.Normal;
    private readonly StringBuilder _csiParams = new();

    // Callbacks
    private readonly Action<byte[]> _send;
    private readonly Func<string, Task> _onCommand;
    private readonly Func<string> _getPrompt;

    // Rate limiting
    private readonly Queue<DateTime> _inputTimes = new();
    private readonly int _maxInputRate;
    private bool _throttled;

    public LineEditor(Action<byte[]> send, Func<string, Task> onCommand, Func<string> getPrompt, int maxInputRate = 20)
    {
        _send = send;
        _onCommand = onCommand;
        _getPrompt = getPrompt;
        _maxInputRate = maxInputRate;
    }

    // Queue for deferred async actions (command execution)
    private readonly Queue<string> _pendingCommands = new();

    /// <summary>
    /// Process incoming raw bytes from SSH channel.
    /// All byte processing is synchronous to avoid splitting escape sequences.
    /// Rate limiting is applied per data chunk, not per byte.
    /// </summary>
    public async Task ProcessBytes(byte[] data)
    {
        // Apply rate limiting once per data chunk
        await CheckRateLimit(data.Length);

        // Process all bytes synchronously — this prevents escape sequence splitting
        foreach (var b in data)
        {
            switch (_state)
            {
                case State.Normal:
                    ProcessNormal(b);
                    break;
                case State.Escape:
                    ProcessEscape(b);
                    break;
                case State.CSI:
                    ProcessCSI(b);
                    break;
            }
        }

        // Execute any queued commands (from Enter/Ctrl+D)
        while (_pendingCommands.Count > 0)
        {
            var cmd = _pendingCommands.Dequeue();
            await _onCommand(cmd);
        }
    }

    private void ProcessNormal(byte b)
    {
        switch (b)
        {
            case 0x1b: // ESC
                _state = State.Escape;
                break;
            case 0x0d: // CR (Enter)
                Send("\r\n");
                var cmd = _buffer.ToString();
                _buffer.Clear();
                _cursorPos = 0;
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    _history.Add(cmd);
                }
                _historyIndex = _history.Count;
                _savedLine = "";
                _pendingCommands.Enqueue(cmd);
                break;
            case 0x0a: // LF - ignore (CR already handled)
                break;
            case 0x7f: // Backspace (most terminals send DEL)
                Backspace();
                break;
            case 0x08: // Ctrl+H (also backspace)
                Backspace();
                break;
            case 0x03: // Ctrl+C
                Send("^C\r\n");
                _buffer.Clear();
                _cursorPos = 0;
                SendPrompt();
                break;
            case 0x04: // Ctrl+D
                if (_buffer.Length == 0)
                {
                    _pendingCommands.Enqueue("exit");
                }
                else
                {
                    // Forward delete (like Delete key) when buffer is not empty
                    DeleteForward();
                }
                break;
            case 0x01: // Ctrl+A (Home)
                MoveToStart();
                break;
            case 0x05: // Ctrl+E (End)
                MoveToEnd();
                break;
            case 0x0c: // Ctrl+L (Clear screen)
                ClearScreen();
                break;
            case 0x15: // Ctrl+U (Kill line before cursor)
                KillLineBeforeCursor();
                break;
            case 0x0b: // Ctrl+K (Kill line after cursor)
                KillLineAfterCursor();
                break;
            case 0x17: // Ctrl+W (Delete word backward)
                DeleteWordBackward();
                break;
            case 0x09: // Tab - insert spaces
                InsertChar(' ');
                InsertChar(' ');
                break;
            case 0x12: // Ctrl+R - ignore (reverse search not implemented)
                break;
            default:
                if (b >= 0x20 && b < 0x7f) // Printable ASCII
                {
                    InsertChar((char)b);
                }
                break;
        }
    }

    private void ProcessEscape(byte b)
    {
        if (b == (byte)'[')
        {
            _state = State.CSI;
            _csiParams.Clear();
        }
        else if (b == (byte)'O')
        {
            // SS3 sequences (some terminals send \eOA etc for arrows)
            _state = State.CSI;
            _csiParams.Clear();
        }
        else
        {
            _state = State.Normal;
        }
    }

    private void ProcessCSI(byte b)
    {
        if (b >= 0x30 && b <= 0x3f) // Parameter bytes (0-9, ;, <, =, >, ?)
        {
            _csiParams.Append((char)b);
        }
        else if (b >= 0x40 && b <= 0x7e) // Final byte
        {
            _state = State.Normal;
            HandleCSI((char)b);
        }
        else
        {
            // Unexpected byte, reset
            _state = State.Normal;
        }
    }

    private void HandleCSI(char final)
    {
        switch (final)
        {
            case 'A': HistoryPrev(); break;       // Up arrow
            case 'B': HistoryNext(); break;       // Down arrow
            case 'C': MoveRight(); break;          // Right arrow
            case 'D': MoveLeft(); break;           // Left arrow
            case 'H': MoveToStart(); break;        // Home
            case 'F': MoveToEnd(); break;          // End
            case '~':
                var param = _csiParams.ToString();
                switch (param)
                {
                    case "1": MoveToStart(); break;  // Home (alternate)
                    case "3": DeleteForward(); break;  // Delete
                    case "4": MoveToEnd(); break;      // End (alternate)
                    case "7": MoveToStart(); break;    // Home (rxvt)
                    case "8": MoveToEnd(); break;      // End (rxvt)
                }
                break;
        }
    }

    private void InsertChar(char c)
    {
        _buffer.Insert(_cursorPos, c);
        _cursorPos++;
        RedrawFromCursor();
    }

    private void Backspace()
    {
        if (_cursorPos > 0)
        {
            _buffer.Remove(_cursorPos - 1, 1);
            _cursorPos--;
            RedrawLine();
        }
    }

    private void DeleteForward()
    {
        if (_cursorPos < _buffer.Length)
        {
            _buffer.Remove(_cursorPos, 1);
            RedrawLine();
        }
    }

    private void MoveLeft()
    {
        if (_cursorPos > 0)
        {
            _cursorPos--;
            Send("\x1b[D");
        }
    }

    private void MoveRight()
    {
        if (_cursorPos < _buffer.Length)
        {
            _cursorPos++;
            Send("\x1b[C");
        }
    }

    private void MoveToStart()
    {
        if (_cursorPos > 0)
        {
            Send($"\x1b[{_cursorPos}D");
            _cursorPos = 0;
        }
    }

    private void MoveToEnd()
    {
        if (_cursorPos < _buffer.Length)
        {
            var diff = _buffer.Length - _cursorPos;
            Send($"\x1b[{diff}C");
            _cursorPos = _buffer.Length;
        }
    }

    private void HistoryPrev()
    {
        if (_history.Count == 0 || _historyIndex <= 0) return;

        if (_historyIndex == _history.Count)
        {
            _savedLine = _buffer.ToString();
        }

        _historyIndex--;
        ReplaceLine(_history[_historyIndex]);
    }

    private void HistoryNext()
    {
        if (_historyIndex >= _history.Count) return;

        _historyIndex++;
        if (_historyIndex == _history.Count)
        {
            ReplaceLine(_savedLine);
        }
        else
        {
            ReplaceLine(_history[_historyIndex]);
        }
    }

    private void ReplaceLine(string newLine)
    {
        // Move to beginning of current input
        if (_cursorPos > 0)
        {
            Send($"\x1b[{_cursorPos}D");
        }
        // Erase from cursor to end of line
        Send("\x1b[K");

        _buffer.Clear();
        _buffer.Append(newLine);
        _cursorPos = newLine.Length;

        Send(newLine);
    }

    private void KillLineBeforeCursor()
    {
        if (_cursorPos > 0)
        {
            _buffer.Remove(0, _cursorPos);
            _cursorPos = 0;
            RedrawLine();
        }
    }

    private void KillLineAfterCursor()
    {
        if (_cursorPos < _buffer.Length)
        {
            _buffer.Remove(_cursorPos, _buffer.Length - _cursorPos);
            // Erase from cursor to end
            Send("\x1b[K");
        }
    }

    private void DeleteWordBackward()
    {
        if (_cursorPos == 0) return;

        var origPos = _cursorPos;
        // Skip trailing spaces
        while (_cursorPos > 0 && _buffer[_cursorPos - 1] == ' ')
        {
            _cursorPos--;
        }
        // Delete until space or start
        while (_cursorPos > 0 && _buffer[_cursorPos - 1] != ' ')
        {
            _cursorPos--;
        }

        _buffer.Remove(_cursorPos, origPos - _cursorPos);
        RedrawLine();
    }

    private void ClearScreen()
    {
        // Clear screen and move cursor to top-left
        Send("\x1b[2J\x1b[H");
        SendPrompt();
        Send(_buffer.ToString());
        // Move cursor back to correct position
        var back = _buffer.Length - _cursorPos;
        if (back > 0)
        {
            Send($"\x1b[{back}D");
        }
    }

    private void RedrawLine()
    {
        // Carriage return
        Send("\r");
        // Redraw prompt + buffer
        Send(_getPrompt());
        Send(_buffer.ToString());
        // Clear trailing chars
        Send("\x1b[K");
        // Move cursor to correct position
        var back = _buffer.Length - _cursorPos;
        if (back > 0)
        {
            Send($"\x1b[{back}D");
        }
    }

    private void RedrawFromCursor()
    {
        // Save cursor position info
        var remaining = _buffer.ToString()[(_cursorPos - 1)..]; // from newly inserted char
        Send(remaining);
        Send("\x1b[K"); // Clear any leftover
        var back = remaining.Length - 1;
        if (back > 0)
        {
            Send($"\x1b[{back}D");
        }
    }

    public void SendPrompt()
    {
        // Ensure cursor is visible
        Send("\x1b[?25h");
        Send(_getPrompt());
    }

    private void Send(string text)
    {
        _send(Encoding.UTF8.GetBytes(text));
    }

    private async Task CheckRateLimit(int byteCount)
    {
        var now = DateTime.UtcNow;

        // Remove timestamps older than 1 second
        while (_inputTimes.Count > 0 && (now - _inputTimes.Peek()).TotalSeconds > 1.0)
        {
            _inputTimes.Dequeue();
        }

        // Count printable chars (escape sequences are multi-byte for single action)
        _inputTimes.Enqueue(now);

        if (_inputTimes.Count > _maxInputRate)
        {
            if (!_throttled)
            {
                _throttled = true;
            }
            // Add artificial delay to simulate high latency
            var delayMs = 50 + Random.Shared.Next(100, 300);
            await Task.Delay(delayMs);
        }
        else
        {
            _throttled = false;
        }
    }
}

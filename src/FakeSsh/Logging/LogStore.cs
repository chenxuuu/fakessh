using System.Collections.Concurrent;
using System.Text.Json;

namespace FakeSsh.Logging;

public class LogStore
{
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ILogger<LogStore> _logger;
    private readonly string _logDir;

    public LogStore(ILogger<LogStore> logger)
    {
        _logger = logger;
        _logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_logDir);

        LoadHistoricalSessions();
    }

    /// <summary>
    /// Load all historical sessions from JSONL log files on disk
    /// </summary>
    private void LoadHistoricalSessions()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logDir, "*.jsonl");
            var loadedCount = 0;

            foreach (var logFile in logFiles)
            {
                var sessionId = Path.GetFileNameWithoutExtension(logFile);

                try
                {
                    var lines = File.ReadAllLines(logFile);
                    if (lines.Length == 0) continue;

                    var events = new List<SessionEvent>();
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var evt = JsonSerializer.Deserialize<SessionEvent>(line);
                            if (evt != null) events.Add(evt);
                        }
                        catch
                        {
                            // Skip malformed lines
                        }
                    }

                    if (events.Count == 0) continue;

                    // Reconstruct SessionInfo from events
                    var session = new SessionInfo
                    {
                        SessionId = sessionId,
                        Events = events
                    };

                    // Extract metadata from events
                    var connectEvent = events.FirstOrDefault(e => e.Type == SessionEventType.Connected);
                    if (connectEvent != null)
                    {
                        session.ClientIp = connectEvent.ClientIp;
                        session.ConnectedAt = connectEvent.Timestamp;
                    }
                    else
                    {
                        session.ConnectedAt = events.First().Timestamp;
                    }

                    var authEvent = events.FirstOrDefault(e => e.Type == SessionEventType.AuthSuccess);
                    if (authEvent != null)
                    {
                        session.Username = authEvent.Username;
                        session.Authenticated = true;
                    }

                    var disconnectEvent = events.LastOrDefault(e => e.Type == SessionEventType.Disconnected);
                    if (disconnectEvent != null)
                    {
                        session.DisconnectedAt = disconnectEvent.Timestamp;
                    }

                    _sessions[sessionId] = session;
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load historical log file: {File}", logFile);
                }
            }

            if (loadedCount > 0)
            {
                _logger.LogInformation("Loaded {Count} historical sessions from disk", loadedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load historical sessions");
        }
    }

    public SessionInfo CreateSession(string sessionId, string? clientIp)
    {
        var session = new SessionInfo
        {
            SessionId = sessionId,
            ClientIp = clientIp,
            ConnectedAt = DateTime.UtcNow
        };
        _sessions[sessionId] = session;
        AddEvent(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Connected,
            ClientIp = clientIp
        });
        return session;
    }

    public void AddEvent(SessionEvent evt)
    {
        if (_sessions.TryGetValue(evt.SessionId, out var session))
        {
            lock (session.Events)
            {
                session.Events.Add(evt);
            }

            if (evt.Type == SessionEventType.AuthSuccess)
            {
                session.Username = evt.Username;
                session.Authenticated = true;

                // Flush all buffered events to disk now that auth succeeded
                FlushSessionToDisk(session);
            }
            else if (evt.Type == SessionEventType.Disconnected)
            {
                session.DisconnectedAt = DateTime.UtcNow;

                if (!session.Authenticated)
                {
                    // Never authenticated — discard entirely
                    _sessions.TryRemove(evt.SessionId, out _);
                    _logger.LogDebug("[{Session}] Unauthenticated session discarded", evt.SessionId[..8]);
                    return;
                }
            }

            // Only write to disk if already authenticated
            if (session.Authenticated)
            {
                AppendEventToDisk(evt);
            }
        }
    }

    private void FlushSessionToDisk(SessionInfo session)
    {
        try
        {
            var logFile = Path.Combine(_logDir, $"{session.SessionId}.jsonl");
            List<SessionEvent> snapshot;
            lock (session.Events)
            {
                snapshot = new List<SessionEvent>(session.Events);
            }
            // Write all buffered events (Connected, auth attempts, AuthSuccess)
            var lines = string.Join(Environment.NewLine, snapshot.Select(e => JsonSerializer.Serialize(e)));
            File.WriteAllText(logFile, lines + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush session log to disk");
        }
    }

    private void AppendEventToDisk(SessionEvent evt)
    {
        try
        {
            var logFile = Path.Combine(_logDir, $"{evt.SessionId}.jsonl");
            // Don't append if the event was already written by FlushSessionToDisk
            // (AuthSuccess event triggers flush which already includes it)
            if (evt.Type == SessionEventType.AuthSuccess) return;

            var json = JsonSerializer.Serialize(evt);
            File.AppendAllText(logFile, json + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write log event");
        }
    }

    public void LogCommand(string sessionId, string? username, string command)
    {
        AddEvent(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Command,
            Username = username,
            Data = command
        });
        _logger.LogInformation("[{Session}] {User}$ {Command}", sessionId[..8], username, command);
    }

    public void LogAiResponse(string sessionId, string? username, string command, string response)
    {
        AddEvent(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.AiResponse,
            Username = username,
            Data = response,
            Extra = command
        });
    }

    public void LogAuth(string sessionId, string? clientIp, string username, bool success)
    {
        AddEvent(new SessionEvent
        {
            SessionId = sessionId,
            Type = success ? SessionEventType.AuthSuccess : SessionEventType.AuthFailed,
            Username = username,
            ClientIp = clientIp
        });
        _logger.LogInformation("[{Session}] Auth {Result} for {User} from {Ip}",
            sessionId[..8], success ? "SUCCESS" : "FAILED", username, clientIp);
    }

    public void LogDisconnect(string sessionId)
    {
        AddEvent(new SessionEvent
        {
            SessionId = sessionId,
            Type = SessionEventType.Disconnected
        });
        _logger.LogInformation("[{Session}] Disconnected", sessionId[..8]);
    }

    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return _sessions.Values
            .OrderByDescending(s => s.ConnectedAt)
            .ToList();
    }

    public SessionInfo? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }
}

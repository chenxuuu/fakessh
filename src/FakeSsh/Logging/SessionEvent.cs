namespace FakeSsh.Logging;

public enum SessionEventType
{
    Connected,
    AuthAttempt,
    AuthSuccess,
    AuthFailed,
    Command,
    AiResponse,
    Disconnected
}

public class SessionEvent
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string SessionId { get; set; } = "";
    public SessionEventType Type { get; set; }
    public string? Username { get; set; }
    public string? ClientIp { get; set; }
    public string? Data { get; set; }
    public string? Extra { get; set; }
}

public class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string? Username { get; set; }
    public string? ClientIp { get; set; }
    public DateTime ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public bool IsActive => DisconnectedAt == null;
    public bool Authenticated { get; set; }
    public List<SessionEvent> Events { get; set; } = new();
}

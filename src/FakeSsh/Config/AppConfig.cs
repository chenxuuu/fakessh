namespace FakeSsh.Config;

public class AppConfig
{
    public int SshPort { get; set; } = 2222;
    public string Hostname { get; set; } = "debian-srv";
    public string HostKeyPath { get; set; } = "host_key.pem";
    public int MaxInputRatePerSecond { get; set; } = 20;
    public int SessionTimeoutSeconds { get; set; } = 120;
    public int MaxOutputLength { get; set; } = 8192;
    public List<UserCredential> Users { get; set; } = new()
    {
        new UserCredential { Username = "root", Password = "toor" }
    };
    public OpenAiConfig OpenAi { get; set; } = new();
}

public class UserCredential
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class OpenAiConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string Model { get; set; } = "gpt-4o";
    public int MaxTokens { get; set; } = 4096;
}

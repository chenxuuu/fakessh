using FakeSsh.AI;
using FakeSsh.Config;
using FakeSsh.Logging;
using FakeSsh.Ssh;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("FakeSsh"));

// Services
builder.Services.AddSingleton<LogStore>();
builder.Services.AddSingleton<ChatService>();

// SSH server as background service
builder.Services.AddHostedService<SshServerHost>();

var host = builder.Build();
host.Run();


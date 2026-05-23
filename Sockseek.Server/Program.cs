using Sockseek.Api;
using Sockseek.Server;

Sockseek.Core.SockseekLog.SetupExceptionHandling();
Sockseek.Core.SockseekLog.AddConsole();

var app = ServerHost.Build(args);
app.Run();

using Sockseek.Api;
using Sockseek.Server;

Sockseek.Core.SockseekLog.SetupExceptionHandling();
Sockseek.Core.SockseekLog.AddConsole();

try
{
    var app = ServerHost.Build(args);
    app.Run();
}
catch (Exception ex)
{
    Sockseek.Core.SockseekLog.Fatal(ex, "Unhandled server error");
}

using Microsoft.Extensions.Logging;
using Sockseek.Core;

namespace Sockseek.Server;

public static class CoreLoggerBridge
{
    public static void Configure(IServiceProvider _, LogLevel minimumLevel)
    {
        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.AddSink(
            (_, message) => Console.WriteLine(message),
            minimumLevel,
            prependDate: true,
            prependLogLevel: true);
    }
}

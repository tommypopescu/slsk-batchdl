namespace Sockseek.Cli;

/// Settings consumed by the CLI launcher when hosting `Sockseek daemon`.
public class DaemonSettings
{
    /// IP/interface used by `Sockseek daemon` for the HTTP/SignalR API.
    public string ListenIp { get; set; } = "127.0.0.1";

    /// Port used by `Sockseek daemon` for the HTTP/SignalR API.
    public int ListenPort { get; set; } = 5030;
}

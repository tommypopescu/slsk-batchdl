using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sockseek.Core;

namespace Tests.Core;

[TestClass]
public class SockseekLogTests
{
    [TestCleanup]
    public void Cleanup()
    {
        SockseekLog.RemoveNonFileOutputs();
        SockseekLog.RemoveFileOutputs();
    }

    [TestMethod]
    public void LogConsoleOnly_DoesNotWriteToNonConsoleSinks()
    {
        SockseekLog.RemoveNonFileOutputs();
        var consoleMessages = new List<string>();
        var sinkMessages = new List<string>();

        SockseekLog.AddConsole(writer: (message, _) => consoleMessages.Add(message));
        SockseekLog.AddSink((_, message) => sinkMessages.Add(message));

        SockseekLog.LogConsoleOnly(LogLevel.Information, "spotify-token=secret");

        CollectionAssert.Contains(consoleMessages, "spotify-token=secret");
        Assert.AreEqual(0, sinkMessages.Count);
    }

    [TestMethod]
    public void NonConsoleLogs_IncludeExplicitCategoryAndLevel()
    {
        var sinkMessages = new List<string>();
        SockseekLog.AddSink((_, message) => sinkMessages.Add(message), LogLevel.Debug, prependLogLevel: true);

        SockseekLog.Info("cli message", categoryName: SockseekLog.Categories.Cli);
        SockseekLog.Debug("core message", callerFilePath: "/repo/Sockseek.Core/DownloadEngine.cs");
        SockseekLog.Warn("daemon message", callerFilePath: "/repo/Sockseek.Server/ServerHost.cs");

        CollectionAssert.AreEqual(new[]
        {
            "[info] [Sockseek.cli] cli message",
            "[debug] [Sockseek.core] core message",
            "[warn] [Sockseek.daemon] daemon message",
        }, sinkMessages);
    }

    [TestMethod]
    public void ConsoleLogs_OmitCategoryForCliRendering()
    {
        var consoleMessages = new List<string>();
        SockseekLog.AddConsole(prependLogLevel: true, writer: (message, _) => consoleMessages.Add(message));

        SockseekLog.Info("plain output", categoryName: SockseekLog.Categories.Cli);

        CollectionAssert.AreEqual(new[]
        {
            "[info] plain output",
        }, consoleMessages);
    }

    [TestMethod]
    public async Task FileLogging_AllowsConcurrentWritesToSameLogFile()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "Sockseek-logger-concurrent-" + Guid.NewGuid() + ".log");

        try
        {
            SockseekLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            await Task.WhenAll(Enumerable.Range(0, 100)
                .Select(i => Task.Run(() => SockseekLog.Debug($"message-{i}"))));

            var lines = File.ReadAllLines(logPath);
            Assert.AreEqual(100, lines.Length);
            CollectionAssert.AreEquivalent(
                Enumerable.Range(0, 100).Select(i => $"[Sockseek.tests.core] message-{i}").ToArray(),
                lines);
        }
        finally
        {
            SockseekLog.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    [TestMethod]
    public void FileLogging_DoesNotThrowWhenLogFileIsLockedByAnotherProcess()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "Sockseek-logger-locked-" + Guid.NewGuid() + ".log");
        File.WriteAllText(logPath, "");

        try
        {
            SockseekLog.AddOrReplaceFile(logPath, LogLevel.Debug, prependDate: false, prependLogLevel: false);

            using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            SockseekLog.Debug("this should not crash");
        }
        finally
        {
            SockseekLog.RemoveFileOutputs();
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }
}

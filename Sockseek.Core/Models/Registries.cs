using System.Collections.Concurrent;
using Sockseek.Core.Services;

namespace Sockseek.Core.Models;
    // TODO: Replace this generic session-state registry with purpose-built services:
    // ActiveDownloadTracker for in-flight transfer state/manual skip/stale cancellation,
    // DownloadedFileCache for per-run duplicate reuse, and UserSuccessTracker for ranking heuristics.
    public interface IDownloadRegistry
    {
        ConcurrentDictionary<string, ActiveDownload> Downloads { get; }
        ConcurrentDictionary<string, FileDownloadResult> DownloadedFiles { get; }
    }

    public interface IUserStats
    {
        ConcurrentDictionary<string, int> UserSuccessCounts { get; }
    }

    public class SessionRegistry : IDownloadRegistry, IUserStats
    {
        public ConcurrentDictionary<string, ActiveDownload> Downloads { get; } = new();
        public ConcurrentDictionary<string, FileDownloadResult> DownloadedFiles { get; } = new();
        public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();
    }

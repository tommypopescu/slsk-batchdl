using System.Collections.Concurrent;
using Sockseek.Core.Jobs;
using Sockseek.Core.Models;
using Sockseek.Core.Services;

namespace Sockseek.Core.Models;
    public interface ISearchRegistry
    {
        ConcurrentDictionary<SongJob, SearchInfo> Searches { get; }
    }

    public interface IDownloadRegistry
    {
        ConcurrentDictionary<string, ActiveDownload> Downloads { get; }
        ConcurrentDictionary<string, FileDownloadResult> DownloadedFiles { get; }
    }

    public interface IUserStats
    {
        ConcurrentDictionary<string, int> UserSuccessCounts { get; }
    }

    public class SessionRegistry : ISearchRegistry, IDownloadRegistry, IUserStats
    {
        public ConcurrentDictionary<SongJob, SearchInfo> Searches { get; } = new();
        public ConcurrentDictionary<string, ActiveDownload> Downloads { get; } = new();
        public ConcurrentDictionary<string, FileDownloadResult> DownloadedFiles { get; } = new();
        public ConcurrentDictionary<string, int> UserSuccessCounts { get; } = new();
    }

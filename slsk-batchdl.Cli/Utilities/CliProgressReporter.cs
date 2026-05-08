using System.Collections.Concurrent;
using Soulseek;
using Sldl.Core;
using Sldl.Core.Jobs;
using Sldl.Core.Models;
using Sldl.Core.Services;
using ProgressBar = Sldl.Cli.IProgressBar;
using Sldl.Core.Settings;
using Sldl.Server;

namespace Sldl.Cli;

public class CliProgressReporter
{
    private readonly CliSettings _cli;
    private readonly TerminalLiveRenderer? _live;

    private readonly ConcurrentDictionary<SongJob, BarData> _bars = new();
    private readonly ConcurrentDictionary<Job, ProgressBar?> _jobBars = new();
    private readonly ConcurrentDictionary<AlbumJob, AlbumBlock> _albumBlocks = new();
    private readonly ConcurrentDictionary<Job, string> _jobStatuses = new();
    private readonly ConcurrentDictionary<Job, int> _jobSpinIndexes = new();
    private readonly ConcurrentDictionary<SongJob, (string text, int pos)> _savedState = new();
    private readonly ConcurrentDictionary<Job, string> _plainJobStatusLines = new();
    private readonly ConcurrentDictionary<Guid, BarData> _backendBars = new();
    private readonly ConcurrentDictionary<Guid, ProgressBar?> _backendJobBars = new();
    private readonly ConcurrentDictionary<Guid, BackendAlbumBlock> _backendAlbumBlocks = new();
    private readonly ConcurrentDictionary<Guid, string> _backendJobStatuses = new();
    private readonly ConcurrentDictionary<Guid, (string text, int pos)> _backendSavedState = new();
    private readonly ConcurrentDictionary<Guid, string> _backendPlainJobStatusLines = new();
    private readonly ConcurrentDictionary<Guid, ServerJobKind> _backendJobKinds = new();
    private readonly ConcurrentDictionary<Guid, Guid> _backendParentJobIds = new();
    private readonly ConcurrentDictionary<Guid, byte> _backendInlineChildJobs = new();
    private readonly ConcurrentDictionary<Guid, (int DisplayId, string Name)> _backendLiveSongInfo = new();
    private readonly ConcurrentDictionary<Guid, byte> _liveTerminalParentLogs = new();
    private readonly ConcurrentDictionary<SongJob, AlbumJob> _songToAlbum = new();
    private readonly ConcurrentDictionary<Guid, Guid> _backendSongToAlbum = new();

    static readonly char[] SpinFrames = { '|', '/', '—', '\\' };

    private bool PlainMode => _cli.NoProgress;

    sealed class BarData
    {
        public ProgressBar? Bar;
        public string       BaseText   = "";
        public string       StateLabel = "";
        public string?      JobPrefix;
        public int          SpinIndex  = 0;
        public int          Pct        = 0;
    }

    sealed class AlbumBlock
    {
        public List<SongJob> Songs = new();
        public SongJob?      LastUpdatedSong;
        public ProgressBar?  CompactBar;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       LastStatus;
    }

    sealed class BackendAlbumBlock
    {
        public JobSummaryDto Summary = default!;
        public List<SongJobPayloadDto> Songs = new();
        public ConcurrentDictionary<Guid, byte> CompletedSongIds = new();
        public Guid?         LastUpdatedSongId;
        public ProgressBar?  CompactBar;
        public int           LastDone = -1;
        public int           LastTotal = -1;
        public string?       LastStatus;
        public int           LastCompactSpin = -1;
    }

    private readonly CancellationTokenSource _tickCts = new();

    private bool _isPaused;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (_live != null)
                _live.IsPaused = value;
        }
    }

    private bool LiveMode => _live != null;

    public bool UsesLiveRendering => LiveMode;

    public CliProgressReporter(CliSettings cli)
    {
        _cli = cli;
        if (!cli.NoProgress && !Console.IsOutputRedirected)
            _live = new TerminalLiveRenderer();
        _ = TickLoopAsync(_tickCts.Token);
    }

    public void Stop()
    {
        _tickCts.Cancel();
        _live?.Dispose();
    }

    internal void Attach(ICliBackend backend)
    {
        backend.EventReceived += envelope =>
        {
            switch (envelope.Type)
            {
                case "job.upserted" when envelope.Payload is JobSummaryDto e:
                    ReportJobUpserted(e);
                    break;
                case "extraction.started" when envelope.Payload is ExtractionStartedEventDto e:
                    ReportExtractionStarted(e);
                    break;
                case "extraction.failed" when envelope.Payload is ExtractionFailedEventDto e:
                    ReportExtractionFailed(e);
                    break;
                case "job.started" when envelope.Payload is JobStartedEventDto e:
                    ReportJobStarted(e);
                    break;
                case "job.status" when envelope.Payload is JobStatusEventDto e:
                    ReportJobStatus(e);
                    break;
                case "job.folder-retrieving" when envelope.Payload is JobFolderRetrievingEventDto e:
                    ReportJobFolderRetrieving(e);
                    break;
                case "song.searching" when envelope.Payload is SongSearchingEventDto e:
                    ReportSongSearching(e);
                    break;
                case "download.started" when envelope.Payload is DownloadStartedEventDto e:
                    ReportDownloadStart(e);
                    break;
                case "download.progress" when envelope.Payload is DownloadProgressEventDto e:
                    ReportDownloadProgress(e);
                    break;
                case "download.state-changed" when envelope.Payload is DownloadStateChangedEventDto e:
                    ReportDownloadStateChanged(e);
                    break;
                case "song.state-changed" when envelope.Payload is SongStateChangedEventDto e:
                    ReportStateChanged(e);
                    break;
                case "album.download-started" when envelope.Payload is AlbumDownloadStartedEventDto e:
                    ReportAlbumDownloadStarted(e);
                    break;
                case "album.track-download-started" when envelope.Payload is AlbumTrackDownloadStartedEventDto e:
                    ReportAlbumTrackDownloadStarted(e);
                    break;
                case "album.download-completed" when envelope.Payload is AlbumDownloadCompletedEventDto e:
                    ReportAlbumDownloadCompleted(e);
                    break;
                case "on-complete.started" when envelope.Payload is OnCompleteStartedEventDto e:
                    ReportOnCompleteStart(e);
                    break;
                case "on-complete.ended" when envelope.Payload is OnCompleteEndedEventDto e:
                    ReportOnCompleteEnd(e);
                    break;
                case "search.rate-limited":
                    if (LiveMode)
                        _live!.SetStatusMessage("Search rate limit reached, waiting...");
                    else
                        Printing.WriteLine("Search rate limit reached, waiting...", ConsoleColor.DarkGray);
                    break;
                case "search.resumed":
                    _live?.SetStatusMessage(null);
                    break;
            }
        };
    }

    private void ReportJobUpserted(JobSummaryDto summary)
    {
        RememberBackendStructure(summary);
        if (!IsInfrastructureJobKind(summary.Kind))
            _live?.UpsertJob(new TerminalJobRecord(
                summary.JobId.ToString(),
                summary.DisplayId,
                GetJobTypePrefix(summary.Kind).TrimEnd(' ', ':'),
                summary.State.ToString(),
                summary.ParentJobId?.ToString()));

        if (LiveMode && IsTerminalJobState(summary.State))
        {
            RemoveLiveJob(summary.JobId);
            if (summary.Kind == ServerJobKind.Album && _backendAlbumBlocks.TryRemove(summary.JobId, out var liveBlock))
            {
                foreach (var song in liveBlock.Songs)
                    if (song.JobId is Guid songJobId)
                        _backendSongToAlbum.TryRemove(songJobId, out _);
            }

            if (summary.Kind != ServerJobKind.Song && _liveTerminalParentLogs.TryAdd(summary.JobId, 0))
            {
                var kind = TerminalKind(summary);
                var label = TerminalStatusLabel(summary.State, summary.FailureReason);
                var name = summary.ItemName ?? "";
                var detail = summary.QueryText ?? name;
                LogLive(kind, summary, $"{label}: {WithName(name, detail)}");
            }

            return;
        }

        if (summary.State == ServerProtocol.JobStates.Failed && summary.Kind != ServerJobKind.Song && summary.Kind != ServerJobKind.Extract)
        {
            ReportJobStatus(new JobStatusEventDto(summary, TerminalStatusLabel(summary.State, summary.FailureReason)));
        }

        if (PlainMode)
            return;

        if (summary.Kind == ServerJobKind.Album
            && IsTerminalJobState(summary.State)
            && _backendAlbumBlocks.TryRemove(summary.JobId, out var block))
        {
            CompleteBackendAlbumBlock(summary.JobId, block, summary);
        }
    }

    async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);
                if (IsPaused) continue;

                foreach (var (_, d) in _bars)
                {
                    if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                    d.SpinIndex++;
                    try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                }

                foreach (var (_, d) in _backendBars)
                {
                    if (d.StateLabel != "InProgress" || d.Bar == null) continue;
                    d.SpinIndex++;
                    try { d.Bar.Refresh(d.Pct, BuildText(d)); } catch { }
                }

                foreach (var (job, block) in _albumBlocks)
                {
                    if (!_jobBars.TryGetValue(job, out var headerBar) || headerBar == null) continue;
                    int done  = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                    int total = block.Songs.Count;
                    _jobStatuses.TryGetValue(job, out var status);

                    if (done != block.LastDone || total != block.LastTotal || status != block.LastStatus)
                    {
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job, done, total, status)); } catch { }
                        block.LastDone = done;
                        block.LastTotal = total;
                        block.LastStatus = status;
                    }

                    if (block.CompactBar != null && block.LastUpdatedSong != null && _bars.TryGetValue(block.LastUpdatedSong, out var d))
                    {
                        if (d.StateLabel == "InProgress") d.SpinIndex++;
                        try { block.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
                    }
                }

                foreach (var (jobId, block) in _backendAlbumBlocks)
                {
                    if (!_backendJobBars.TryGetValue(jobId, out var headerBar) || headerBar == null) continue;
                    int done  = BackendAlbumDoneCount(block);
                    int total = block.Songs.Count;
                    _backendJobStatuses.TryGetValue(jobId, out var status);

                    if (done != block.LastDone || total != block.LastTotal || status != block.LastStatus)
                    {
                        try { headerBar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(block.Summary, done, total, status)); } catch { }
                        block.LastDone = done;
                        block.LastTotal = total;
                        block.LastStatus = status;
                    }

                    if (block.CompactBar != null && block.LastUpdatedSongId != null && _backendBars.TryGetValue(block.LastUpdatedSongId.Value, out var d))
                    {
                        if (d.StateLabel == "InProgress") d.SpinIndex++;
                        try { block.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
                    }
                }



            }
        }
        catch (OperationCanceledException) { }
    }


    // ── bar text construction ────────────────────────────────────────────

    static string BuildText(BarData d, bool indent = false)
    {
        string prefix = d.StateLabel == "InProgress"
            ? (indent ? "  " : "") + $"{SpinFrames[d.SpinIndex % SpinFrames.Length]} "
            : (indent ? "    " : "  ");
        var stateLabel = d.JobPrefix != null
            ? $"{d.JobPrefix}{d.StateLabel.ToLowerInvariant()}"
            : d.StateLabel;
        string label = (stateLabel + ":").PadRight(d.JobPrefix != null ? 0 : 12);
        return $"{prefix}{label} {d.BaseText}";
    }

    private int BackendAlbumDoneCount(BackendAlbumBlock block)
    {
        int done = 0;
        foreach (var song in block.Songs)
        {
            if (song.JobId is not Guid jobId)
                continue;

            if (block.CompletedSongIds.ContainsKey(jobId))
            {
                done++;
                continue;
            }

            if (_backendBars.TryGetValue(jobId, out var data) && IsTerminalBar(data))
                done++;
        }

        return done;
    }

    private static bool IsTerminalBar(BarData data)
        => data.StateLabel is "Succeeded" or "Already exists" or "Not found"
            || data.StateLabel.StartsWith("Failed", StringComparison.Ordinal);

    private void MarkBackendAlbumTrackCompleted(Guid songJobId)
    {
        foreach (var block in _backendAlbumBlocks.Values)
        {
            if (block.Songs.Any(song => song.JobId == songJobId))
                block.CompletedSongIds.TryAdd(songJobId, 0);
        }
    }

    private string AlbumHeaderText(AlbumJob job, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{job.DisplayId}] AlbumJob: {job.ToString(true)}{statusStr}  [{done}/{total}]";
    }

    private static string AlbumHeaderText(JobSummaryDto summary, int done, int total, string? status = null)
    {
        string statusStr = !string.IsNullOrEmpty(status) ? $" ({status})" : "";
        return $"[{summary.DisplayId}] AlbumJob: {summary.QueryText}{statusStr}  [{done}/{total}]";
    }

    private static string WithName(string name, string detail)
        => detail == name ? detail : $"{name}: {detail}";

    private static bool IsInfrastructureJobKind(ServerJobKind kind)
        => kind is ServerJobKind.Extract or ServerJobKind.JobList or ServerJobKind.RetrieveFolder
            or ServerJobKind.Aggregate or ServerJobKind.AlbumAggregate;

    private static string GetJobTypePrefix(Job job) => job switch
    {
        RetrieveFolderJob => "RetrieveFolderJob: ",
        _                 => job.GetType().Name + ": "
    };

    private static string GetJobTypePrefix(ServerJobKind kind)
    {
        if (kind == ServerJobKind.RetrieveFolder)
            return "RetrieveFolderJob: ";
        if (kind == ServerJobKind.JobList)
            return "JobList: ";
        if (kind == ServerJobKind.AlbumAggregate)
            return "AlbumAggregateJob: ";

        string kindText = kind.ToWireString();
        return $"{char.ToUpperInvariant(kindText[0])}{kindText[1..]}Job: ";
    }

    private static string ProfileSuffix(Job job)
    {
        var profiles = job.Config?.AppliedAutoProfiles;
        return profiles?.Count > 0 ? $" [{string.Join(", ", profiles)}]" : "";
    }

    private static string ProfileSuffix(JobSummaryDto summary)
        => summary.AppliedAutoProfiles.Count > 0 ? $" [{string.Join(", ", summary.AppliedAutoProfiles)}]" : "";

    private static string TextWithProfileSuffix(Job job, string text)
        => text + ProfileSuffix(job);

    private static string TextWithProfileSuffix(JobSummaryDto summary, string text)
        => text + ProfileSuffix(summary);

    private static string JobStatusLine(Job job, string status, string? detail = null)
        => $"[{job.DisplayId}] {GetJobTypePrefix(job)}{status}: {detail ?? job.ToString(true)}";

    private static string JobStatusLine(JobSummaryDto summary, string status, string? detail = null)
    {
        var name = summary.ItemName ?? "";
        var d = detail ?? summary.QueryText ?? name;
        return $"[{summary.DisplayId}] {GetJobTypePrefix(summary.Kind)}{status}: {WithName(name, d)}";
    }

    private void UpsertLiveJob(Job job, string state, int? percent = null, int? done = null, int? total = null, IReadOnlyList<JobChildView>? children = null)
    {
        if (_live == null) return;
        _live.Upsert(new JobView(
            job.Id.ToString(),
            job.DisplayId,
            GetJobTypePrefix(job).TrimEnd(' ', ':'),
            job.ToString(true),
            state,
            percent,
            done,
            total,
            children));
    }

    private void UpsertLiveJob(JobSummaryDto summary, string state, int? percent = null, int? done = null, int? total = null, IReadOnlyList<JobChildView>? children = null)
    {
        if (_live == null) return;
        _live.Upsert(new JobView(
            summary.JobId.ToString(),
            summary.DisplayId,
            GetJobTypePrefix(summary.Kind).TrimEnd(' ', ':'),
            summary.QueryText ?? summary.ItemName ?? "",
            state,
            percent,
            done,
            total,
            children));
    }

    private void RemoveLiveJob(Job job) => _live?.Remove(job.Id.ToString());

    private void RemoveLiveJob(Guid jobId) => _live?.Remove(jobId.ToString());

    private void LogLive(TerminalLogKind kind, Job job, string message)
        => _live?.Log(new TerminalLogLine(kind, job.Id.ToString(), job.DisplayId, GetJobTypePrefix(job).TrimEnd(' ', ':'), message));

    private void LogLive(TerminalLogKind kind, JobSummaryDto summary, string message)
        => _live?.Log(new TerminalLogLine(kind, summary.JobId.ToString(), summary.DisplayId, GetJobTypePrefix(summary.Kind).TrimEnd(' ', ':'), message));

    private void LogLiveSong(TerminalLogKind kind, Guid jobId, int displayId, string message)
        => _live?.Log(new TerminalLogLine(kind, jobId.ToString(), displayId, "SongJob", message));

    private static TerminalLogKind TerminalKind(SongJob song)
        => song.State switch
        {
            JobState.Done => TerminalLogKind.SongDownloaded,
            JobState.AlreadyExists => TerminalLogKind.SongAlreadyExists,
            JobState.Skipped or JobState.NotFoundLastTime => TerminalLogKind.SongSkipped,
            JobState.Failed when song.FailureReason == FailureReason.Cancelled => TerminalLogKind.JobCancelled,
            _ => TerminalLogKind.SongFailed,
        };

    private static TerminalLogKind TerminalKind(SongStateChangedEventDto song)
        => song.State switch
        {
            ServerProtocol.JobStates.Done => TerminalLogKind.SongDownloaded,
            ServerProtocol.JobStates.AlreadyExists => TerminalLogKind.SongAlreadyExists,
            ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime => TerminalLogKind.SongSkipped,
            ServerProtocol.JobStates.Failed when song.FailureReason == ServerProtocol.FailureReasons.Cancelled => TerminalLogKind.JobCancelled,
            _ => TerminalLogKind.SongFailed,
        };

    private static TerminalLogKind TerminalKind(JobSummaryDto summary)
        => summary.Kind switch
        {
            ServerJobKind.JobList when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.PlaylistCompleted,
            ServerJobKind.Aggregate when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.AggregateCompleted,
            _ when summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists
                => TerminalLogKind.JobSucceeded,
            _ when summary.FailureReason == ServerProtocol.FailureReasons.Cancelled
                => TerminalLogKind.JobCancelled,
            _ => TerminalLogKind.JobFailed,
        };

    private bool ShouldLogLiveSongTerminal(SongStateChangedEventDto song)
    {
        if (!IsBackendJobListChild(song.JobId))
            return true;

        return song.State is not (
            ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Skipped
            or ServerProtocol.JobStates.NotFoundLastTime);
    }

    private void UpsertLiveAlbum(AlbumJob job, AlbumBlock block)
    {
        if (_live == null) return;

        int done = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
        int total = block.Songs.Count;
        _jobStatuses.TryGetValue(job, out var status);

        var children = block.Songs
            .Where(song => song.State is not (JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped or JobState.Pending))
            .Select(song =>
            {
                _bars.TryGetValue(song, out var data);
                return new JobChildView(
                    song.Id.ToString(),
                    song.DisplayId,
                    data?.StateLabel ?? song.State.ToString(),
                    data?.BaseText ?? song.ToString(true),
                    data?.Pct);
            })
            .ToList();

        UpsertLiveJob(job, status ?? "downloading", total > 0 ? done * 100 / total : null, done, total, children);
    }

    private void UpsertLiveBackendAlbum(Guid albumId, BackendAlbumBlock block)
    {
        if (_live == null) return;

        int done = BackendAlbumDoneCount(block);
        int total = block.Songs.Count;
        _backendJobStatuses.TryGetValue(albumId, out var status);

        var children = block.Songs
            .Where(song => song.JobId.HasValue && !block.CompletedSongIds.ContainsKey(song.JobId.Value))
            .Select(song =>
            {
                var jobId = song.JobId!.Value;
                _backendBars.TryGetValue(jobId, out var data);
                return new JobChildView(
                    jobId.ToString(),
                    song.DisplayId ?? 0,
                    data?.StateLabel ?? song.State?.ToString() ?? "pending",
                    data?.BaseText ?? song.ResolvedFilename ?? SongQueryText(song.Query),
                    data?.Pct);
            })
            .Where(child => child.State is not "Pending")
            .ToList();

        UpsertLiveJob(block.Summary, status ?? "downloading", total > 0 ? done * 100 / total : null, done, total, children);
    }

    private static void WriteJobLineWithProfileSuffix(Job job, string text, ConsoleColor mainColor = ConsoleColor.Gray)
    {
        string suffix = ProfileSuffix(job);
        if (suffix.Length == 0)
        {
            Logger.Info(text, mainColor);
            return;
        }

        Logger.LogNonConsole(Logger.LogLevel.Info, text + suffix);
        Printing.Write(text, mainColor);
            Printing.WriteLine(suffix, ConsoleColor.DarkGray);
    }

    private void WritePlainJobStatus(Job job, string status, string? detail = null)
    {
        _jobStatuses[job] = status;
        var line = JobStatusLine(job, status, detail);
        if (_plainJobStatusLines.TryGetValue(job, out var previous) && previous == line)
            return;

        _plainJobStatusLines[job] = line;
        WriteJobLineWithProfileSuffix(job, line);
    }

    private void WritePlainJobStatus(JobSummaryDto summary, string status, string? detail = null)
    {
        _backendJobStatuses[summary.JobId] = status;
        var line = JobStatusLine(summary, status, detail);
        if (_backendPlainJobStatusLines.TryGetValue(summary.JobId, out var previous) && previous == line)
            return;

        _backendPlainJobStatusLines[summary.JobId] = line;
        RefreshOrPrintJobLineWithProfileSuffix(null, 0, summary, line, print: true);
    }

    private void WritePlainBackendSongStatus(
        Guid jobId,
        int displayId,
        SongQueryDto query,
        string status,
        string? detail = null)
    {
        var name = SongQueryText(query);
        var line = $"[{displayId}] SongJob: {status}: {WithName(name, detail ?? name)}";
        if (_backendPlainJobStatusLines.TryGetValue(jobId, out var previous) && previous == line)
            return;

        _backendJobStatuses[jobId] = status;
        _backendPlainJobStatusLines[jobId] = line;
        Logger.Info(line);
    }

    private void ClearPlainJobStatus(Job job)
    {
        _jobStatuses.TryRemove(job, out _);
        _plainJobStatusLines.TryRemove(job, out _);
    }

    private void ClearPlainJobStatus(JobSummaryDto summary)
    {
        _backendJobStatuses.TryRemove(summary.JobId, out _);
        _backendPlainJobStatusLines.TryRemove(summary.JobId, out _);
    }

    private static void RefreshOrPrintJobLineWithProfileSuffix(ProgressBar? progress, int current, Job job, string text, bool print = false, bool refreshIfOffscreen = false)
    {
        var textWithSuffix = TextWithProfileSuffix(job, text);
        Printing.RefreshOrPrint(progress, current, textWithSuffix, print, refreshIfOffscreen);
    }

    private static void RefreshOrPrintJobLineWithProfileSuffix(ProgressBar? progress, int current, JobSummaryDto summary, string text, bool print = false, bool refreshIfOffscreen = false)
    {
        var textWithSuffix = TextWithProfileSuffix(summary, text);
        Printing.RefreshOrPrint(progress, current, textWithSuffix, print, refreshIfOffscreen);
    }

    static string FailureReasonLabel(FailureReason reason) => reason switch
    {
        FailureReason.NoSuitableFileFound  => "No suitable file found",
        FailureReason.InvalidSearchString  => "Invalid search string",
        FailureReason.OutOfDownloadRetries => "Out of download retries",
        FailureReason.AllDownloadsFailed   => "All downloads failed",
        FailureReason.ExtractionFailed     => "Extraction failed",
        FailureReason.Cancelled            => "Cancelled",
        FailureReason.Other                => "Unknown error",
        _                                  => "",
    };

    static string FailureReasonLabel(ServerFailureReason? reason) => reason switch
    {
        ServerFailureReason.NoSuitableFileFound  => "No suitable file found",
        ServerFailureReason.InvalidSearchString  => "Invalid search string",
        ServerFailureReason.OutOfDownloadRetries => "Out of download retries",
        ServerFailureReason.AllDownloadsFailed   => "All downloads failed",
        ServerFailureReason.ExtractionFailed     => "Extraction failed",
        ServerFailureReason.Cancelled            => "Cancelled",
        ServerFailureReason.Other                => "Unknown error",
        _                                        => "",
    };

    private static string GetStateLabel(TransferStates s)
    {
        if (s.HasFlag(TransferStates.InProgress))   return "InProgress";
        if (s.HasFlag(TransferStates.Queued))
            return s.HasFlag(TransferStates.Remotely) ? "Queued (R)" :
                   s.HasFlag(TransferStates.Locally)  ? "Queued (L)" : "Queued";
        if (s.HasFlag(TransferStates.Initializing)) return "Initialising";
        return "Requested";
    }

    private static bool IsTerminalJobState(ServerJobState state)
        => state is ServerProtocol.JobStates.Done
            or ServerProtocol.JobStates.AlreadyExists
            or ServerProtocol.JobStates.Failed
            or ServerProtocol.JobStates.Skipped;

    private void RememberBackendStructure(JobSummaryDto summary)
    {
        _backendJobKinds[summary.JobId] = summary.Kind;
        if (summary.ParentJobId is Guid parentJobId)
            _backendParentJobIds[summary.JobId] = parentJobId;
        else
            _backendParentJobIds.TryRemove(summary.JobId, out _);

        if (IsBackendInlineChild(summary))
            _backendInlineChildJobs[summary.JobId] = 0;
        else
            _backendInlineChildJobs.TryRemove(summary.JobId, out _);
    }

    private bool IsBackendInlineChild(Guid jobId)
        => _backendInlineChildJobs.ContainsKey(jobId);

    private bool IsBackendJobListChild(Guid jobId)
        => _backendParentJobIds.TryGetValue(jobId, out var parentJobId)
            && _backendJobKinds.TryGetValue(parentJobId, out var parentKind)
            && parentKind == ServerJobKind.JobList;

    private bool IsBackendInlineChild(JobSummaryDto summary)
        => summary.Kind == ServerJobKind.Song
            && summary.ParentJobId is Guid parentJobId
            && _backendJobKinds.TryGetValue(parentJobId, out var parentKind)
            && parentKind is ServerJobKind.Album;

    private static string SongDisplay(SongJob song)
    {
        var chosen = song.ChosenCandidate;
        if (chosen != null)
            return Printing.DisplayString(song.Query, chosen.File, chosen.Response, infoFirst: false);
        if (!string.IsNullOrEmpty(song.DownloadPath))
            return $"{song.ToString(true)} at {song.DownloadPath}";
        return song.ToString(true);
    }

    private static string SongDisplay(SongStateChangedEventDto song)
    {
        var chosen = song.ChosenCandidate;
        if (chosen != null)
            return CandidateDisplay(chosen);
        if (!string.IsNullOrEmpty(song.DownloadPath))
            return $"{SongQueryText(song.Query)} at {song.DownloadPath}";
        return SongQueryText(song.Query);
    }

    private static string CandidateDisplay(FileCandidateRefDto candidate)
        => $"{candidate.Username}\\..\\{candidate.Filename.Replace('/', '\\').TrimStart('\\')}";

    private static string CandidateDisplay(FileCandidateDto candidate)
        => CandidateDisplay(candidate.Ref);

    private static string SongQueryText(SongQueryDto query)
    {
        var parts = new[] { query.Artist, query.Title }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length > 0)
            return string.Join(" - ", parts);

        return query.Album ?? query.Uri ?? "";
    }


    private static string TerminalStatusLabel(JobState state, FailureReason reason, string fallbackStatus = "failed")
    {
        if (state == JobState.Done)
            return "succeeded";
        if (state == JobState.AlreadyExists)
            return "already exists";

        var reasonLabel = FailureReasonLabel(reason);
        if (reasonLabel.Length == 0) reasonLabel = "Unknown error";
        return $"{fallbackStatus}[{reasonLabel}]";
    }

    private static string TerminalStatusLabel(SongJob song)
    {
        if (song.State == JobState.Done)
            return "succeeded";
        if (song.State == JobState.AlreadyExists)
            return "already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0 && song.State is JobState.Failed or JobState.Skipped or JobState.NotFoundLastTime)
            reason = "Unknown error";

        var discovery = song.Discovery != null && song.Discovery.LockedFileCount > 0 
            ? $" (Found {song.Discovery.LockedFileCount} locked files)" 
            : "";
        return reason.Length > 0 ? $"failed [{reason}]{discovery}" : $"failed{discovery}";
    }

    private static string TerminalStatusLabel(ServerJobState state, ServerFailureReason? reason, string fallbackStatus = "failed")
    {
        if (state == ServerJobState.Done)
            return "succeeded";
        if (state == ServerJobState.AlreadyExists)
            return "already exists";

        var reasonLabel = FailureReasonLabel(reason);
        if (reasonLabel.Length == 0) reasonLabel = "Unknown error";
        return $"{fallbackStatus} [{reasonLabel}]";
    }

    private static string TerminalStatusLabel(SongStateChangedEventDto song)
    {
        if (song.State == ServerProtocol.JobStates.Done)
            return "succeeded";
        if (song.State == ServerProtocol.JobStates.AlreadyExists)
            return "already exists";

        var reason = FailureReasonLabel(song.FailureReason);
        if (reason.Length == 0 && song.State is ServerProtocol.JobStates.Failed or ServerProtocol.JobStates.Skipped or ServerProtocol.JobStates.NotFoundLastTime)
            reason = "Unknown error";

        var discovery = song.DiscoveryLockedFileCount > 0 
            ? $" (Found {song.DiscoveryLockedFileCount} locked files)" 
            : "";
        return reason.Length > 0 ? $"failed [{reason}]{discovery}" : $"failed{discovery}";
    }


    // ── event handlers ───────────────────────────────────────────────────

    private void ReportDownloadStart(SongJob song, FileCandidate candidate)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(
                song,
                "downloading",
                Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false));
            return;
        }

        if (LiveMode)
        {
            if (_songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
            {
                var liveData = _bars.GetOrAdd(song, _ => new BarData());
                liveData.StateLabel = "queued";
                liveData.BaseText = Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false);
                liveData.Pct = 0;
                UpsertLiveAlbum(albumJob, block);
            }
            else
            {
                UpsertLiveJob(song, "queued", 0);
            }
            return;
        }

        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Queued";
        d.BaseText   = Printing.DisplayString(song.Query, candidate.File, candidate.Response, infoFirst: false);
        bool isCompact = _cli.AlbumCompactProgress && _songToAlbum.ContainsKey(song);
        UpdateLastUpdated(song);
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d, indent: isCompact), print: d.Bar != null);
    }

    private void ReportDownloadStart(DownloadStartedEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                "downloading",
                CandidateDisplay(song.Candidate));
            return;
        }

        if (LiveMode)
        {
            if (IsBackendInlineChild(song.JobId))
            {
                if (_backendBars.TryGetValue(song.JobId, out var childData)
                    && _backendSongToAlbum.TryGetValue(song.JobId, out var albumId)
                    && _backendAlbumBlocks.TryGetValue(albumId, out var block))
                {
                    childData.StateLabel = "queued";
                    childData.BaseText = CandidateDisplay(song.Candidate);
                    childData.Pct = 0;
                    UpsertLiveBackendAlbum(albumId, block);
                }
                return;
            }

            var songName = WithName(SongQueryText(song.Query), CandidateDisplay(song.Candidate));
            _backendLiveSongInfo[song.JobId] = (song.DisplayId, songName);
            _live?.Upsert(new JobView(
                song.JobId.ToString(),
                song.DisplayId,
                "SongJob",
                songName,
                "queued",
                0));
            return;
        }

        if (IsBackendInlineChild(song.JobId) && !_backendBars.ContainsKey(song.JobId))
            return;

        var d = _backendBars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = IsBackendInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Queued";
        d.BaseText = CandidateDisplay(song.Candidate);
        bool isCompact = _cli.AlbumCompactProgress && _backendSongToAlbum.ContainsKey(song.JobId);
        UpdateLastUpdatedBackend(song.JobId);
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d, indent: isCompact), print: !IsBackendInlineChild(song.JobId) && d.Bar != null);
    }

    private void ReportDownloadProgress(SongJob song, long bytesTransferred, long totalBytes)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            int pct = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0;
            if (_songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
                UpsertLiveAlbum(albumJob, block);
            else
                UpsertLiveJob(song, "downloading", pct);
            return;
        }

        if (!_bars.TryGetValue(song, out var d)) return;
        d.Pct = totalBytes > 0 ? (int)(bytesTransferred * 100 / totalBytes) : 0;
        UpdateLastUpdated(song);
    }

    private void ReportDownloadProgress(DownloadProgressEventDto progress)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            int pct = progress.TotalBytes > 0 ? (int)(progress.BytesTransferred * 100 / progress.TotalBytes) : 0;
            if (_backendSongToAlbum.TryGetValue(progress.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
            {
                if (_backendBars.TryGetValue(progress.JobId, out var childData))
                    childData.Pct = pct;
                UpsertLiveBackendAlbum(albumId, block);
            }
            else if (!IsBackendInlineChild(progress.JobId) && _backendLiveSongInfo.TryGetValue(progress.JobId, out var info))
                _live?.Upsert(new JobView(progress.JobId.ToString(), info.DisplayId, "SongJob", info.Name, "downloading", pct));
            return;
        }

        if (!_backendBars.TryGetValue(progress.JobId, out var d)) return;
        d.Pct = progress.TotalBytes > 0 ? (int)(progress.BytesTransferred * 100 / progress.TotalBytes) : 0;
        UpdateLastUpdatedBackend(progress.JobId);
    }

    private void ReportStateChanged(SongJob song)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(song, TerminalStatusLabel(song), WithName(song.ToString(true), SongDisplay(song)));
            return;
        }

        if (LiveMode)
        {
            if (_songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
            {
                UpsertLiveAlbum(albumJob, block);
                LogLive(TerminalKind(song) == TerminalLogKind.SongDownloaded ? TerminalLogKind.AlbumTrackDownloaded : TerminalLogKind.AlbumTrackFailed,
                    albumJob,
                    $"{TerminalStatusLabel(song)}: {WithName(song.ToString(true), SongDisplay(song))}");
            }
            else
            {
                RemoveLiveJob(song);
                LogLive(TerminalKind(song), song, $"{TerminalStatusLabel(song)}: {WithName(song.ToString(true), SongDisplay(song))}");
            }
            return;
        }

        Logger.LogNonConsole(Logger.LogLevel.Info, $"[{song.DisplayId}] SongJob: {TerminalStatusLabel(song)}: {SongDisplay(song)}");

        if (_bars.TryGetValue(song, out var d))
        {
            UpdateLastUpdated(song);
            bool succeeded = song.State is JobState.Done or JobState.AlreadyExists;
            d.StateLabel = song.State switch
            {
                JobState.Done => "Succeeded",
                JobState.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (succeeded)
            {
                d.Pct = 100;
                var display = SongDisplay(song);
                if (display != d.BaseText)
                    d.BaseText = display;
            }
            else
            {
                var reason = FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!d.BaseText.Contains($"[{reason}]"))
                    d.BaseText += $"[{reason}]";
            }
            if (d.Bar != null)
            {
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            else if (_cli.AlbumCompactProgress && _songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var b) && b.CompactBar != null)
            {
                try { b.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
            }
        }
        _bars.TryRemove(song, out _);
        _savedState.TryRemove(song, out _);
    }

    private void ReportStateChanged(SongStateChangedEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                TerminalStatusLabel(song),
                SongDisplay(song));
            return;
        }

        if (LiveMode)
        {
            MarkBackendAlbumTrackCompleted(song.JobId);
            if (_backendSongToAlbum.TryGetValue(song.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
            {
                UpsertLiveBackendAlbum(albumId, block);
                LogLive(TerminalKind(song) == TerminalLogKind.SongDownloaded ? TerminalLogKind.AlbumTrackDownloaded : TerminalLogKind.AlbumTrackFailed,
                    block.Summary,
                    $"{TerminalStatusLabel(song)}: {WithName(SongQueryText(song.Query), SongDisplay(song))}");
            }
            else
            {
                RemoveLiveJob(song.JobId);
                _backendLiveSongInfo.TryRemove(song.JobId, out _);
                if (ShouldLogLiveSongTerminal(song))
                    LogLiveSong(TerminalKind(song), song.JobId, song.DisplayId, $"{TerminalStatusLabel(song)}: {WithName(SongQueryText(song.Query), SongDisplay(song))}");
            }
            _backendBars.TryRemove(song.JobId, out _);
            _backendSavedState.TryRemove(song.JobId, out _);
            return;
        }

        if (!IsBackendInlineChild(song.JobId))
            Logger.LogNonConsole(Logger.LogLevel.Info, $"[{song.DisplayId}] SongJob: {TerminalStatusLabel(song)}: {SongDisplay(song)}");

        if (_backendBars.TryGetValue(song.JobId, out var d))
        {
            UpdateLastUpdatedBackend(song.JobId);
            bool succeeded = song.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;
            d.StateLabel = song.State switch
            {
                ServerProtocol.JobStates.Done => "Succeeded",
                ServerProtocol.JobStates.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (succeeded)
            {
                d.Pct = 100;
                var display = SongDisplay(song);
                if (display != d.BaseText)
                    d.BaseText = display;
            }
            else
            {
                var reason = FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!d.BaseText.Contains($"[{reason}]"))
                    d.BaseText += $" [{reason}]";
            }
            MarkBackendAlbumTrackCompleted(song.JobId);
            if (d.Bar != null)
            {
                Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d), print: false);
            }
            else if (_cli.AlbumCompactProgress && _backendSongToAlbum.TryGetValue(song.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var b) && b.CompactBar != null)
            {
                try { b.CompactBar.Refresh(d.Pct, BuildText(d, indent: true)); } catch { }
            }
        }
        _backendBars.TryRemove(song.JobId, out _);
        _backendSavedState.TryRemove(song.JobId, out _);
    }


    // ── display event handlers ───────────────────────────────────────────

    private void ReportExtractionStarted(ExtractJob job)
    {
        if (LiveMode)
        {
            UpsertLiveJob(job, "extracting");
            return;
        }

        if (job.InputType.HasValue)
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                WriteJobLineWithProfileSuffix(job, $"[{job.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}");
            }
        }
    }

    private void ReportExtractionStarted(ExtractionStartedEventDto job)
    {
        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, "extracting");
            return;
        }

        if (!string.IsNullOrWhiteSpace(job.InputType))
        {
            lock (Printing.ConsoleLock)
            {
                Printing.WriteLine();
                RefreshOrPrintJobLineWithProfileSuffix(
                    null,
                    0,
                    job.Summary,
                    $"[{job.Summary.DisplayId}] ExtractJob: Input ({job.InputType}): {job.Input}",
                    print: true);
            }
        }
    }

    private void ReportExtractionCompleted(ExtractJob job, Job result) { }

    private void ReportExtractionFailed(ExtractJob job, string reason)
    {
        if (LiveMode)
        {
            RemoveLiveJob(job);
            LogLive(TerminalLogKind.JobFailed, job, $"failed: {reason}");
            return;
        }

        Logger.Error($"[{job.DisplayId}] ExtractJob: Failed: {job.Input}\n  Reason:    {reason}");
        _jobBars.TryRemove(job, out _);
    }

    private void ReportExtractionFailed(ExtractionFailedEventDto job)
    {
        if (LiveMode)
        {
            RemoveLiveJob(job.Summary.JobId);
            LogLive(TerminalLogKind.JobFailed, job.Summary, $"failed: {job.Reason}");
            return;
        }

        Logger.Error($"[{job.Summary.DisplayId}] ExtractJob: Failed: {job.Summary.QueryText}\n  Reason:    {job.Reason}");
        _backendJobBars.TryRemove(job.Summary.JobId, out _);
    }

    private void ReportJobStarted(Job job)
    {
        if (PlainMode)
        {
            string plainStatus = job is RetrieveFolderJob ? "retrieving folder" : "searching";
            WritePlainJobStatus(job, plainStatus);
            return;
        }

        if (job is SongJob)
            return;

        if (LiveMode)
        {
            string liveStatus = job switch
            {
                RetrieveFolderJob => "retrieving folder",
                AlbumJob aj when aj.ResolvedTarget != null => "downloading",
                _ => "searching"
            };
            UpsertLiveJob(job, liveStatus);
            return;
        }

        var bar = Printing.GetProgressBar();
        _jobBars[job] = bar;
        string status = job switch
        {
            RetrieveFolderJob => "retrieving folder",
            AlbumJob aj when aj.ResolvedTarget != null => "downloading",
            _ => "searching"
        };
        _jobStatuses[job] = status;
        RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, JobStatusLine(job, status), print: true);
    }

    private void ReportJobStarted(JobStartedEventDto job)
    {
        RememberBackendStructure(job.Summary);
        if (IsBackendInlineChild(job.Summary))
            return;

        string status = job.Summary.Kind == ServerJobKind.RetrieveFolder ? "retrieving folder" : "searching";

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, status);
            return;
        }

        if (job.Summary.Kind == ServerJobKind.Song)
            return;

        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, status);
            return;
        }

        var bar = Printing.GetProgressBar();
        _backendJobBars[job.Summary.JobId] = bar;
        _backendJobStatuses[job.Summary.JobId] = status;
        RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job.Summary, JobStatusLine(job.Summary, status), print: true);
    }

    private void ReportJobSearching(Job job)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, "searching");
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(job, "searching");
            return;
        }

        _jobStatuses[job] = "searching";
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, JobStatusLine(job, "searching"), print: false);
        }
    }

    private void ReportJobDownloading(Job job)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, "downloading");
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(job, "downloading");
            return;
        }

        _jobStatuses[job] = "downloading";
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            RefreshOrPrintJobLineWithProfileSuffix(bar, 0, job, JobStatusLine(job, "downloading"), print: false);
        }
    }

    private void ReportAlbumDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        if (PlainMode)
        {
            _jobStatuses[job] = "downloading";
            WriteJobLineWithProfileSuffix(job, JobStatusLine(job, "downloading"));
            return;
        }

        if (Console.IsOutputRedirected)
        {
            Printing.PrintAlbum(folder);
            return;
        }

        int total = folder.Files.Count;

        string ancestor = Utils.GreatestCommonDirectorySlsk(folder.Files
            .Where(f => f.ResolvedTarget != null)
            .Select(f => f.ResolvedTarget!.Filename));

        if (LiveMode)
        {
            if (_albumBlocks.TryGetValue(job, out var oldBlock) && !ReferenceEquals(oldBlock.Songs, folder.Files))
            {
                _albumBlocks.TryRemove(job, out _);
                foreach (var s in oldBlock.Songs)
                {
                    _songToAlbum.TryRemove(s, out _);
                    _bars.TryRemove(s, out _);
                }
            }

            _jobStatuses[job] = "downloading";
            var block = new AlbumBlock { Songs = folder.Files.ToList() };
            foreach (var song in block.Songs)
            {
                _songToAlbum[song] = job;
                string filename = song.ResolvedTarget?.Filename ?? song.Query.ToString();
                string shortName = ancestor.Length > 0
                    ? filename.Replace(ancestor, "").TrimStart('\\')
                    : System.IO.Path.GetFileName(filename);
                string baseText = song.ResolvedTarget != null
                    ? Printing.DisplayString(song.Query, song.ResolvedTarget.File, song.ResolvedTarget.Response, customPath: shortName, showUser: false)
                    : shortName;
                _bars[song] = new BarData { BaseText = baseText, StateLabel = song.State.ToString(), Pct = 0 };
            }

            _albumBlocks[job] = block;
            UpsertLiveAlbum(job, block);
            return;
        }

        lock (Printing.ConsoleLock)
        {
            if (_albumBlocks.TryGetValue(job, out var oldBlock) && !ReferenceEquals(oldBlock.Songs, folder.Files))
            {
                _albumBlocks.TryRemove(job, out _);
                _jobBars.TryRemove(job, out _);
                foreach (var s in oldBlock.Songs)
                {
                    _songToAlbum.TryRemove(s, out _);
                    _bars.TryRemove(s, out _);
                }
            }

            var headerBar = _jobBars.GetOrAdd(job, _ => Printing.GetProgressBar());
            _jobStatuses[job] = "downloading";
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job, AlbumHeaderText(job, 0, total, "downloading"), print: true); } catch { }

            if (!_cli.AlbumCompactProgress)
                Printing.PrintAlbumHeader(folder);

            var block = new AlbumBlock { Songs = folder.Files.ToList() };
            if (_cli.AlbumCompactProgress)
                block.CompactBar = Printing.GetProgressBar();

            foreach (var song in block.Songs)
            {
                _songToAlbum[song] = job;
                string filename  = song.ResolvedTarget?.Filename ?? song.Query.ToString();
                string shortName = ancestor.Length > 0
                    ? filename.Replace(ancestor, "").TrimStart('\\')
                    : System.IO.Path.GetFileName(filename);
                string baseText = song.ResolvedTarget != null
                    ? Printing.DisplayString(song.Query, song.ResolvedTarget.File, song.ResolvedTarget.Response, customPath: shortName, showUser: false)
                    : shortName;

                var bar = _cli.AlbumCompactProgress ? null : Printing.GetProgressBar();
                var d   = new BarData { Bar = bar, BaseText = baseText, StateLabel = "Pending", Pct = 0 };
                _bars[song] = d;
                if (bar != null)
                    try { bar.Refresh(0, BuildText(d)); } catch { }
            }

            if (block.CompactBar != null && block.Songs.Count > 0 && _bars.TryGetValue(block.Songs[0], out var d0))
            {
                try { block.CompactBar.Refresh(d0.Pct, BuildText(d0, indent: true)); } catch { }
            }

            _albumBlocks[job] = block;
        }
    }

    private void ReportAlbumDownloadStarted(AlbumDownloadStartedEventDto job)
    {
        if (PlainMode)
        {
            _backendJobStatuses[job.Summary.JobId] = "downloading";
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                JobStatusLine(job.Summary, "downloading"),
                print: true);
            return;
        }

        if (Console.IsOutputRedirected)
        {
            Printing.WriteLine();
            return;
        }

        if (LiveMode)
        {
            _backendJobStatuses[job.Summary.JobId] = "downloading";
            InitializeBackendAlbumBlock(job.Summary, job.Tracks);
            UpsertLiveBackendAlbum(job.Summary.JobId, _backendAlbumBlocks[job.Summary.JobId]);
            return;
        }

        int total = job.Tracks?.Count ?? job.Folder.Files?.Count ?? 0;
        lock (Printing.ConsoleLock)
        {
            if (_backendAlbumBlocks.TryGetValue(job.Summary.JobId, out var oldBlock))
            {
                _backendAlbumBlocks.TryRemove(job.Summary.JobId, out _);
                _backendJobBars.TryRemove(job.Summary.JobId, out _);
                foreach (var s in oldBlock.Songs)
                {
                    if (s.JobId.HasValue)
                    {
                        _backendSongToAlbum.TryRemove(s.JobId.Value, out _);
                        _backendBars.TryRemove(s.JobId.Value, out _);
                    }
                }
            }

            var headerBar = _backendJobBars.GetOrAdd(job.Summary.JobId, _ => Printing.GetProgressBar());
            _backendJobStatuses[job.Summary.JobId] = "downloading";
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading"), print: true); } catch { }

            if (!_cli.AlbumCompactProgress)
                Printing.PrintAlbumHeader(ToAlbumFolder(job.Folder));
            InitializeBackendAlbumBlock(job.Summary, job.Tracks);
        }
    }

    private void ReportAlbumTrackDownloadStarted(AlbumJob job, AlbumFolder folder)
    {
        if (_albumBlocks.ContainsKey(job))
            return;

        string folderName = string.IsNullOrWhiteSpace(folder.FolderPath)
            ? job.ToString(true)
            : folder.FolderPath;

        if (PlainMode)
        {
            WriteJobLineWithProfileSuffix(job, JobStatusLine(job, "downloading tracks", $"{job.ToString(true)} - {folderName}"));
            return;
        }

        if (LiveMode)
        {
            _jobStatuses[job] = "downloading tracks";
            ReportAlbumDownloadStarted(job, folder);
            if (_albumBlocks.TryGetValue(job, out var block))
                UpsertLiveAlbum(job, block);
            return;
        }

        Printing.WriteLine();
        WriteJobLineWithProfileSuffix(job, JobStatusLine(job, "downloading tracks"));
        Printing.WriteLine($"Folder: {folderName}", ConsoleColor.DarkGray);
    }

    private void ReportAlbumTrackDownloadStarted(AlbumTrackDownloadStartedEventDto job)
    {
        if (_backendAlbumBlocks.ContainsKey(job.Summary.JobId))
            return;

        string folderName = string.IsNullOrWhiteSpace(job.Folder.FolderPath)
            ? job.Summary.QueryText ?? ""
            : job.Folder.FolderPath;

        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                $"[{job.Summary.DisplayId}] AlbumJob: downloading tracks: {job.Summary.QueryText} - {folderName}",
                print: true);
            return;
        }

        if (LiveMode)
        {
            _backendJobStatuses[job.Summary.JobId] = "downloading tracks";
            InitializeBackendAlbumBlock(job.Summary, job.Tracks);
            UpsertLiveBackendAlbum(job.Summary.JobId, _backendAlbumBlocks[job.Summary.JobId]);
            return;
        }

        _backendJobStatuses[job.Summary.JobId] = "downloading tracks";
        InitializeBackendAlbumBlock(job.Summary, job.Tracks);

        if (_backendJobBars.TryGetValue(job.Summary.JobId, out var headerBar) && headerBar != null)
        {
            int total = job.Tracks?.Count ?? 0;
            try { RefreshOrPrintJobLineWithProfileSuffix(headerBar, 0, job.Summary, AlbumHeaderText(job.Summary, 0, total, "downloading tracks"), print: true); } catch { }
        }
        else
        {
            RefreshOrPrintJobLineWithProfileSuffix(null, 0, job.Summary, JobStatusLine(job.Summary, "downloading tracks"), print: true);
        }

        Printing.WriteLine($"Folder: {folderName}", ConsoleColor.DarkGray);
    }

    private void ReportAlbumDownloadCompleted(AlbumJob job)
    {
        if (PlainMode)
        {
            _jobStatuses.TryRemove(job, out _);
            return;
        }

        if (LiveMode)
        {
            if (_albumBlocks.TryRemove(job, out var liveBlock))
            {
                foreach (var s in liveBlock.Songs)
                    _songToAlbum.TryRemove(s, out _);
            }
            RemoveLiveJob(job);
            _jobStatuses.TryRemove(job, out _);
            LogLive(job.State == JobState.Done ? TerminalLogKind.JobSucceeded : TerminalLogKind.JobFailed, job, $"{TerminalStatusLabel(job.State, job.FailureReason)}: {job.ToString(true)}");
            return;
        }

        if (_albumBlocks.TryGetValue(job, out var block))
        {
            foreach (var s in block.Songs)
                _songToAlbum.TryRemove(s, out _);

            int total = block.Songs.Count;
            if (_jobBars.TryGetValue(job, out var headerBar) && headerBar != null)
            {
                _jobStatuses.TryGetValue(job, out var status);
                try { headerBar.Refresh(100, AlbumHeaderText(job, total, total, status)); } catch { }
            }
            _albumBlocks.TryRemove(job, out _);
        }
        _jobBars.TryRemove(job, out _);
        _jobStatuses.TryRemove(job, out _);
        _jobSpinIndexes.TryRemove(job, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
        {
            Printing.WriteLine();
        }
    }

    private void ReportAlbumDownloadCompleted(AlbumDownloadCompletedEventDto job)
    {
        if (PlainMode)
        {
            _backendJobStatuses.TryRemove(job.Summary.JobId, out _);
            return;
        }

        if (LiveMode)
        {
            if (_backendAlbumBlocks.TryRemove(job.Summary.JobId, out var liveBlock))
            {
                foreach (var s in liveBlock.Songs)
                    if (s.JobId.HasValue) _backendSongToAlbum.TryRemove(s.JobId.Value, out _);
            }
            RemoveLiveJob(job.Summary.JobId);
            _backendJobStatuses.TryRemove(job.Summary.JobId, out _);
            LogLive(job.Summary.State == ServerProtocol.JobStates.Done ? TerminalLogKind.JobSucceeded : TerminalLogKind.JobFailed, job.Summary, $"{TerminalStatusLabel(job.Summary.State, job.Summary.FailureReason)}: {job.Summary.QueryText}");
            return;
        }

        if (_backendAlbumBlocks.TryRemove(job.Summary.JobId, out var block))
        {
            foreach (var s in block.Songs)
                if (s.JobId.HasValue) _backendSongToAlbum.TryRemove(s.JobId.Value, out _);

            CompleteBackendAlbumBlock(job.Summary.JobId, block, job.Summary);
        }
        _backendJobBars.TryRemove(job.Summary.JobId, out _);
        _backendJobStatuses.TryRemove(job.Summary.JobId, out _);

        if (!Console.IsOutputRedirected && !_cli.NoProgress)
            Printing.WriteLine();
    }

    private void CompleteBackendAlbumBlock(Guid albumJobId, BackendAlbumBlock block, JobSummaryDto summary)
    {
        CompleteRemainingBackendAlbumBars(block, summary);
        int total = block.Songs.Count;
        if (_backendJobBars.TryGetValue(albumJobId, out var headerBar) && headerBar != null)
        {
            _backendJobStatuses.TryGetValue(albumJobId, out var status);
            try { headerBar.Refresh(100, AlbumHeaderText(summary, total, total, status)); } catch { }
        }
        _backendJobBars.TryRemove(albumJobId, out _);
        _backendJobStatuses.TryRemove(albumJobId, out _);
    }

    private void InitializeBackendAlbumBlock(JobSummaryDto summary, IReadOnlyList<SongJobPayloadDto>? tracks)
    {
        var block = new BackendAlbumBlock { Summary = summary, Songs = tracks?.ToList() ?? [] };
        if (_cli.AlbumCompactProgress && !LiveMode)
        {
            var compactBar = Printing.GetProgressBar();
            block.CompactBar = compactBar;
            if (compactBar != null && block.Songs.Count > 0 && block.Songs[0].JobId is Guid firstJobId && _backendBars.TryGetValue(firstJobId, out var d0))
            {
                try { compactBar.Refresh(d0.Pct, BuildText(d0, indent: true)); } catch { }
            }
        }

        foreach (var song in block.Songs.Where(s => s.JobId.HasValue))
        {
            var songJobId = song.JobId!.Value;
            _backendSongToAlbum[songJobId] = summary.JobId;
            string filename = song.ResolvedFilename ?? $"{song.Query.Artist} - {song.Query.Title}";
            string shortName = System.IO.Path.GetFileName(filename);
            var bar = LiveMode || _cli.AlbumCompactProgress ? null : Printing.GetProgressBar();
            var data = ToBarData(song, bar, shortName);
            _backendBars[songJobId] = data;
            if (bar != null)
                try { bar.Refresh(data.Pct, BuildText(data)); } catch { }
        }

        _backendAlbumBlocks[summary.JobId] = block;
    }

    private void CompleteRemainingBackendAlbumBars(BackendAlbumBlock block, JobSummaryDto summary)
    {
        foreach (var song in block.Songs.Where(song => song.JobId.HasValue))
        {
            block.CompletedSongIds.TryAdd(song.JobId!.Value, 0);

            if (!_backendBars.TryGetValue(song.JobId!.Value, out var data))
                continue;

            bool albumSucceeded = summary.State is ServerProtocol.JobStates.Done or ServerProtocol.JobStates.AlreadyExists;
            data.StateLabel = summary.State switch
            {
                ServerProtocol.JobStates.Done => "Succeeded",
                ServerProtocol.JobStates.AlreadyExists => "Already exists",
                _ => "Failed",
            };
            if (albumSucceeded)
            {
                data.Pct = 100;
            }
            else
            {
                var reason = FailureReasonLabel(summary.FailureReason) is { Length: > 0 } summaryReason
                    ? summaryReason
                    : FailureReasonLabel(song.FailureReason);
                if (reason.Length == 0) reason = "Unknown error";
                if (!data.BaseText.Contains($"[{reason}]", StringComparison.Ordinal))
                    data.BaseText += $" [{reason}]";
            }

            if (data.Bar != null)
                Printing.RefreshOrPrint(data.Bar, data.Pct, BuildText(data), print: false);
            _backendBars.TryRemove(song.JobId.Value, out _);
            _backendSavedState.TryRemove(song.JobId.Value, out _);
        }
    }

    private void ReportJobFolderRetrieving(Job job)
    {
        if (PlainMode)
        {
            WriteJobLineWithProfileSuffix(job, JobStatusLine(job, "retrieving folder"));
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(job, "retrieving folder");
            return;
        }

        _jobBars.TryGetValue(job, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }

    private void ReportJobFolderRetrieving(JobFolderRetrievingEventDto job)
    {
        if (PlainMode)
        {
            RefreshOrPrintJobLineWithProfileSuffix(
                null,
                0,
                job.Summary,
                JobStatusLine(job.Summary, "retrieving folder"),
                print: true);
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(job.Summary, "retrieving folder");
            return;
        }

        _backendJobBars.TryGetValue(job.Summary.JobId, out var bar);
        Printing.RefreshOrPrint(bar, 0, "Getting all files in folder..", print: true);
    }


    private void ReportSongSearching(SongJob song)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(song, "searching");
            return;
        }

        if (LiveMode)
        {
            if (_songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
            {
                var data = _bars.GetOrAdd(song, _ => new BarData());
                data.StateLabel = "searching";
                data.BaseText = song.ToString();
                UpsertLiveAlbum(albumJob, block);
            }
            else
            {
                UpsertLiveJob(song, "searching");
            }
            return;
        }

        if (_bars.TryGetValue(song, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.JobPrefix = $"[{song.DisplayId}] SongJob: ";
            existing.BaseText   = song.ToString();
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        bool isFirst = !_bars.ContainsKey(song);
        var d = _bars.GetOrAdd(song, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Searching";
        d.BaseText   = song.ToString();
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportSongSearching(SongSearchingEventDto song)
    {
        if (PlainMode)
        {
            WritePlainBackendSongStatus(
                song.JobId,
                song.DisplayId,
                song.Query,
                "searching");
            return;
        }

        if (LiveMode)
        {
            var name = SongQueryText(song.Query);
            if (_backendSongToAlbum.TryGetValue(song.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
            {
                var data = _backendBars.GetOrAdd(song.JobId, _ => new BarData());
                data.StateLabel = "searching";
                data.BaseText = name;
                UpsertLiveBackendAlbum(albumId, block);
            }
            else if (!IsBackendInlineChild(song.JobId))
            {
                _backendLiveSongInfo[song.JobId] = (song.DisplayId, name);
                _live?.Upsert(new JobView(song.JobId.ToString(), song.DisplayId, "SongJob", name, "searching"));
            }
            return;
        }

        if (_backendBars.TryGetValue(song.JobId, out var existing))
        {
            existing.StateLabel = "Searching";
            existing.JobPrefix = IsBackendInlineChild(song.JobId) ? null : $"[{song.DisplayId}] SongJob: ";
            existing.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
            Printing.RefreshOrPrint(existing.Bar, 0, BuildText(existing), print: false);
            return;
        }

        if (IsBackendInlineChild(song.JobId))
            return;

        bool isFirst = !_backendBars.ContainsKey(song.JobId);
        var d = _backendBars.GetOrAdd(song.JobId, _ => new BarData { Bar = Printing.GetProgressBar() });
        d.JobPrefix = $"[{song.DisplayId}] SongJob: ";
        d.StateLabel = "Searching";
        d.BaseText = $"{song.Query.Artist} - {song.Query.Title}";
        Printing.RefreshOrPrint(d.Bar, 0, BuildText(d), print: isFirst);
    }

    private void ReportDownloadStateChanged(SongJob song, string stateLabel)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            if (_bars.TryGetValue(song, out var data))
                data.StateLabel = stateLabel;
            if (_songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
                UpsertLiveAlbum(albumJob, block);
            else
                UpsertLiveJob(song, stateLabel.ToLowerInvariant(), _bars.TryGetValue(song, out var liveData) ? liveData.Pct : null);
            return;
        }

        if (!_bars.TryGetValue(song, out var d)) return;
        d.StateLabel = stateLabel;
        bool isCompact = _cli.AlbumCompactProgress && _songToAlbum.ContainsKey(song);
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d, indent: isCompact), print: false);
    }

    private void ReportDownloadStateChanged(DownloadStateChangedEventDto song)
    {
        if (PlainMode) return;

        if (LiveMode)
        {
            string stateLabel = GetStateLabel(Enum.TryParse<TransferStates>(song.State, out var transferState) ? transferState : TransferStates.None);
            if (_backendBars.TryGetValue(song.JobId, out var data))
                data.StateLabel = stateLabel;
            if (_backendSongToAlbum.TryGetValue(song.JobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
                UpsertLiveBackendAlbum(albumId, block);
            else if (_backendLiveSongInfo.TryGetValue(song.JobId, out var info))
                _live?.Upsert(new JobView(song.JobId.ToString(), info.DisplayId, "SongJob", info.Name, stateLabel.ToLowerInvariant()));
            return;
        }

        if (!_backendBars.TryGetValue(song.JobId, out var d)) return;
        d.StateLabel = GetStateLabel(Enum.TryParse<TransferStates>(song.State, out var state) ? state : TransferStates.None);
        bool isCompact = _cli.AlbumCompactProgress && _backendSongToAlbum.ContainsKey(song.JobId);
        Printing.RefreshOrPrint(d.Bar, d.Pct, BuildText(d, indent: isCompact), print: false);
    }

    private void ReportOnCompleteStart(SongJob song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete start: {song}");
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(song, "on-complete");
            return;
        }

        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        _savedState[song] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"{song}");
    }

    private void ReportOnCompleteStart(OnCompleteStartedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete start: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (LiveMode)
        {
            var name = SongQueryText(song.Query);
            _backendLiveSongInfo[song.JobId] = (song.DisplayId, name);
            _live?.Upsert(new JobView(song.JobId.ToString(), song.DisplayId, "SongJob", name, "on-complete"));
            return;
        }

        if (!_backendBars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        _backendSavedState[song.JobId] = (d.Bar.Line1 ?? "", d.Bar.Current);
        Printing.RefreshOrPrint(d.Bar, d.Bar.Current, "  OnComplete:  " + $"[{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
    }

    private void ReportOnCompleteEnd(SongJob song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete end: {song}");
            return;
        }

        if (LiveMode)
        {
            UpsertLiveJob(song, "downloading");
            return;
        }

        if (!_bars.TryGetValue(song, out var d) || d.Bar == null) return;
        if (_savedState.TryGetValue(song, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportOnCompleteEnd(OnCompleteEndedEventDto song)
    {
        if (PlainMode)
        {
            Logger.Info($"OnComplete end: [{song.DisplayId}] {song.Query.Artist} - {song.Query.Title}");
            return;
        }

        if (LiveMode)
        {
            if (_backendLiveSongInfo.TryGetValue(song.JobId, out var info))
                _live?.Upsert(new JobView(song.JobId.ToString(), info.DisplayId, "SongJob", info.Name, "downloading"));
            return;
        }

        if (!_backendBars.TryGetValue(song.JobId, out var d) || d.Bar == null) return;
        if (_backendSavedState.TryGetValue(song.JobId, out var saved))
            Printing.RefreshOrPrint(d.Bar, saved.pos, saved.text);
    }

    private void ReportJobStatus(Job job, string status)
    {
        if (PlainMode)
        {
            WritePlainJobStatus(job, status);
            return;
        }

        if (LiveMode)
        {
            _jobStatuses[job] = status;
            if (job is AlbumJob aj && _albumBlocks.TryGetValue(aj, out var block))
                UpsertLiveAlbum(aj, block);
            else
                UpsertLiveJob(job, status);
            return;
        }

        _jobStatuses[job] = status;
        if (_jobBars.TryGetValue(job, out var bar) && bar != null)
        {
            if (job is AlbumJob aj && _albumBlocks.TryGetValue(aj, out var block))
            {
                int done = block.Songs.Count(s => s.State is JobState.Done or JobState.AlreadyExists or JobState.Failed or JobState.Skipped);
                int total = block.Songs.Count;
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(aj, done, total, status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, JobStatusLine(job, status), print: false);
            }
        }
    }

    private void ReportJobStatus(JobStatusEventDto job)
    {
        RememberBackendStructure(job.Summary);
        if (IsBackendInlineChild(job.Summary))
            return;

        if (PlainMode)
        {
            WritePlainJobStatus(job.Summary, job.Status);
            return;
        }

        if (LiveMode)
        {
            _backendJobStatuses[job.Summary.JobId] = job.Status;
            if (job.Summary.Kind == ServerJobKind.Album && _backendAlbumBlocks.TryGetValue(job.Summary.JobId, out var block))
                UpsertLiveBackendAlbum(job.Summary.JobId, block);
            else
                UpsertLiveJob(job.Summary, job.Status);
            return;
        }

        _backendJobStatuses[job.Summary.JobId] = job.Status;
        if (_backendJobBars.TryGetValue(job.Summary.JobId, out var bar) && bar != null)
        {
            if (job.Summary.Kind == ServerJobKind.Album && _backendAlbumBlocks.TryGetValue(job.Summary.JobId, out var block))
            {
                int total = block.Songs.Count;
                int done = BackendAlbumDoneCount(block);
                try { bar.Refresh(total > 0 ? done * 100 / total : 0, AlbumHeaderText(job.Summary, done, total, job.Status)); } catch { }
            }
            else
            {
                Printing.RefreshOrPrint(bar, 0, JobStatusLine(job.Summary, job.Status), print: false);
            }
        }
    }

    private static AlbumFolder ToAlbumFolder(AlbumFolderDto folder)
        => new(
            folder.Username,
            folder.FolderPath,
            folder.Files?.Select(ToSongJob).ToList() ?? [])
        {
            IsFullyRetrieved = folder.IsFullyRetrieved,
        };

    private static SongJob ToSongJob(FileCandidateDto file)
    {
        var candidate = ToFileCandidate(file);
        var query = Searcher.InferSongQuery(candidate.Filename, new SongQuery());
        return new SongJob(query) { ResolvedTarget = candidate };
    }

    private static FileCandidate ToFileCandidate(FileCandidateDto candidate)
        => new(
            new SearchResponse(
                candidate.Username,
                -1,
                candidate.Peer.HasFreeUploadSlot ?? false,
                candidate.Peer.UploadSpeed ?? -1,
                -1,
                null),
            new Soulseek.File(
                0,
                candidate.Filename,
                candidate.Size,
                candidate.Extension ?? Path.GetExtension(candidate.Filename),
                candidate.Attributes?.Select(x => new Soulseek.FileAttribute(Enum.Parse<Soulseek.FileAttributeType>(x.Type), x.Value))));

    private static SongJob ToSongJob(SongJobPayloadDto dto)
    {
        var job = new SongJob(new SongQuery
        {
            Artist = dto.Query.Artist ?? "",
            Title = dto.Query.Title ?? "",
            Album = dto.Query.Album ?? "",
            URI = dto.Query.Uri ?? "",
            Length = dto.Query.Length ?? -1,
            ArtistMaybeWrong = dto.Query.ArtistMaybeWrong,
        })
        {
            ResolvedTarget = dto.ResolvedUsername != null && dto.ResolvedFilename != null
                ? new FileCandidate(
                    new SearchResponse(
                        dto.ResolvedUsername,
                        -1,
                        dto.ResolvedHasFreeUploadSlot ?? false,
                        dto.ResolvedUploadSpeed ?? -1,
                        -1,
                        null),
                    new Soulseek.File(
                        1,
                        dto.ResolvedFilename,
                        dto.ResolvedSize ?? 0,
                        dto.ResolvedExtension ?? System.IO.Path.GetExtension(dto.ResolvedFilename),
                        dto.ResolvedAttributes?.Select(x => new FileAttribute(Enum.Parse<FileAttributeType>(x.Type), x.Value))))
                : null
        };

        if (dto.State != null && Enum.TryParse<JobState>(dto.State.Value.ToString(), out var state))
        {
            if (state == JobState.Failed)
            {
                Enum.TryParse<FailureReason>(dto.FailureReason?.ToString(), out var failureReason);
                job.Fail(failureReason, dto.FailureMessage);
                job.DownloadPath = dto.DownloadPath;
            }
            else if (state is JobState.Skipped or JobState.NotFoundLastTime)
            {
                Enum.TryParse<FailureReason>(dto.FailureReason?.ToString(), out var failureReason);
                job.SetSkipped(state, failureReason);
                job.DownloadPath = dto.DownloadPath;
            }
            else if (state == JobState.AlreadyExists)
            {
                job.SetAlreadyExists(dto.DownloadPath);
            }
            else if (state == JobState.Done)
            {
                job.SetDone(dto.DownloadPath);
            }
            else
            {
                job.UpdateState(state);
                job.DownloadPath = dto.DownloadPath;
            }
        }

        return job;
    }

    private static BarData ToBarData(SongJobPayloadDto song, ProgressBar? bar, string shortName)
    {
        var data = new BarData { Bar = bar, BaseText = shortName, StateLabel = "Pending", Pct = 0 };

        if (song.State == ServerProtocol.JobStates.Done)
        {
            data.StateLabel = "Succeeded";
            data.Pct = 100;
        }
        else if (song.State == ServerProtocol.JobStates.AlreadyExists)
        {
            data.StateLabel = "Already exists";
            data.Pct = 100;
        }
        else if (song.State == ServerProtocol.JobStates.Failed)
        {
            data.StateLabel = "Failed";
            if (song.FailureReason != null)
                data.BaseText += $" [{FailureReasonLabel(song.FailureReason)}]";
        }
        else if (song.State == ServerProtocol.JobStates.Downloading)
        {
            data.StateLabel = "InProgress";
        }
        else if (song.State == ServerProtocol.JobStates.Searching)
        {
            data.StateLabel = "Searching";
        }

        return data;
    }

    private void UpdateLastUpdated(SongJob song)
    {
        if (_cli.AlbumCompactProgress && _songToAlbum.TryGetValue(song, out var albumJob) && _albumBlocks.TryGetValue(albumJob, out var block))
            block.LastUpdatedSong = song;
    }

    private void UpdateLastUpdatedBackend(Guid songJobId)
    {
        if (_cli.AlbumCompactProgress && _backendSongToAlbum.TryGetValue(songJobId, out var albumId) && _backendAlbumBlocks.TryGetValue(albumId, out var block))
            block.LastUpdatedSongId = songJobId;
    }
}

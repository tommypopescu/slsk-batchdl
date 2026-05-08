using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sldl.Cli;

internal enum TerminalLogKind
{
    JobSucceeded,
    JobFailed,
    JobCancelled,
    SongDownloaded,
    SongAlreadyExists,
    SongSkipped,
    SongFailed,
    AlbumTrackDownloaded,
    AlbumTrackSkipped,
    AlbumTrackFailed,
    ExtractedJobs,
    PlaylistCompleted,
    AggregateCompleted,
    Status,
}

internal sealed record TerminalLogLine(
    TerminalLogKind Kind,
    string JobId,
    int DisplayId,
    string JobType,
    string Message);

internal sealed record JobChildView(
    string Id,
    int DisplayId,
    string State,
    string Name,
    int? Percent = null);

internal sealed record JobView(
    string Id,
    int DisplayId,
    string Kind,
    string Name,
    string State,
    int? Percent = null,
    int? DoneChildren = null,
    int? TotalChildren = null,
    IReadOnlyList<JobChildView>? Children = null,
    string? ParentId = null,
    bool IsParentSummary = false)
{
    public IReadOnlyList<JobChildView> Children { get; init; } = Children ?? [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed record TerminalJobRecord(
    string Id,
    int DisplayId,
    string Kind,
    string State,
    string? ParentId);

internal sealed class TerminalLiveRenderer : IDisposable
{
    private readonly ConcurrentDictionary<string, JobView> _jobs = new();
    private readonly ConcurrentDictionary<string, TerminalJobRecord> _knownJobs = new();
    private readonly ConcurrentQueue<TerminalLogLine> _logs = new();
    private readonly ConcurrentQueue<string> _rawLogs = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _renderTask;
    private readonly TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(100);
    private readonly Lock _sync = new();

    private volatile bool _paused;
    private volatile string? _statusMessage;
    private int _spinFrame;
    private bool _disposed;

    private static readonly IReadOnlyList<string> SpinFrames = SupportsUnicodeSpinner()
        ? Spinner.Known.Dots.Frames
        : ["|", "/", "-", "\\"];

    private static bool SupportsUnicodeSpinner()
    {
        if (!AnsiConsole.Profile.Capabilities.Unicode)
            return false;
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("WT_SESSION") is null)
            return false;
        return true;
    }

    public TerminalLiveRenderer()
    {
        Printing.LiveWriteLine = (line, _) => EnqueueRawLog(line);
        _renderTask = Task.Run(RenderLoopAsync);
    }

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    public void SetStatusMessage(string? message)
    {
        if (_disposed) return;
        _statusMessage = message;
    }

    public void Upsert(JobView job)
    {
        if (_disposed) return;
        _jobs[job.Id] = job with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    public void UpsertJob(TerminalJobRecord job)
    {
        if (_disposed) return;
        _knownJobs[job.Id] = job;
    }

    public void Remove(string id)
    {
        if (_disposed) return;
        _jobs.TryRemove(id, out _);
    }

    public void Log(TerminalLogLine line)
    {
        if (_disposed) return;
        _logs.Enqueue(line);
    }

    public void EnqueueRawLog(string line)
    {
        if (_disposed) return;
        _rawLogs.Enqueue(line);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _renderTask.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        Printing.LiveWriteLine = null;
        var counts = CountKnownJobs(0);
        AnsiConsole.MarkupLine(BuildCountsMarkup(counts));
        _cts.Dispose();
    }

    private async Task RenderLoopAsync()
    {
        try
        {
            await AnsiConsole.Live(Render())
                .AutoClear(true)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Top)
                .StartAsync(async ctx =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        if (!_paused)
                        {
                            FlushLogs();
                            ctx.UpdateTarget(Render());
                            ctx.Refresh();
                        }

                        try { await Task.Delay(_refreshInterval, _cts.Token); }
                        catch (OperationCanceledException) { }
                    }

                    FlushLogs();
                    ctx.UpdateTarget(Render());
                    ctx.Refresh();
                });
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void FlushLogs()
    {
        lock (_sync)
        {
            while (_rawLogs.TryDequeue(out var rawLine))
            {
                int pad = Console.IsOutputRedirected ? 0 : Math.Max(0, Console.WindowWidth - 1 - rawLine.Length);
                AnsiConsole.WriteLine(rawLine + new string(' ', pad));
            }

            while (_logs.TryDequeue(out var line))
            {
                var formatted = FormatLog(line);
                int pad = Console.IsOutputRedirected ? 0 : Math.Max(0, Console.WindowWidth - 1 - formatted.Length);
                AnsiConsole.WriteLine(formatted + new string(' ', pad));
            }
        }
    }

    private Rows Render()
    {
        var rows = BuildRows();
        // First row is the status line and uses markup; the rest are plain text
        // (job names may contain '[' which would break markup parsing).
        var renderables = rows.Select((row, i) => i == 0
            ? (IRenderable)new Markup(row)
            : new Text(row));
        return new Rows(renderables);
    }

    private List<string> BuildRows()
    {
        int maxRows = MaxLiveRows();
        var allJobs = _jobs.Values.ToDictionary(job => job.Id, StringComparer.Ordinal);
        var visibleIds = allJobs.Values
            .Where(job => IsLiveState(job.State))
            .Select(job => job.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in visibleIds.ToArray())
            AddVisibleAncestors(id, allJobs, visibleIds);

        var jobs = allJobs.Values
            .Where(job => visibleIds.Contains(job.Id))
            .OrderBy(job => job.ParentId ?? job.Id, StringComparer.Ordinal)
            .ThenBy(job => job.ParentId == null ? 0 : 1)
            .ThenBy(job => job.DisplayId)
            .ToList();

        var counts = CountKnownJobs(jobs.Count);
        var spin = SpinFrames[_spinFrame++ % SpinFrames.Count];

        var statusLine = $"{spin} {BuildCountsMarkup(counts)}";
        if (_statusMessage is string msg)
            statusLine += $" | [bold yellow]{Markup.Escape(msg)}[/]";

        var lines = new List<string>
        {
            statusLine,
            "",
        };

        foreach (var job in jobs)
        {
            var indent = job.ParentId != null ? "  " : "";
            lines.Add(indent + FormatJob(job));
            foreach (var child in job.Children.Where(child => IsLiveState(child.State)))
                lines.Add($"  {indent}  {FormatChild(child)}");
        }

        if (lines.Count == 2)
            lines.Add("(none)");

        if (lines.Count <= maxRows)
            return lines;

        int keep = Math.Max(3, maxRows - 1);
        int omitted = lines.Count - keep;
        return [
            ..lines.Take(2),
            $"... {omitted} active rows hidden ...",
            ..lines.Skip(lines.Count - Math.Max(1, keep - 2)).Take(maxRows - 3),
        ];
    }

    private static void AddVisibleAncestors(
        string id,
        IReadOnlyDictionary<string, JobView> jobs,
        ISet<string> visibleIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (jobs.TryGetValue(id, out var job)
            && job.ParentId is string parentId
            && jobs.ContainsKey(parentId)
            && seen.Add(parentId))
        {
            visibleIds.Add(parentId);
            id = parentId;
        }
    }

    private static bool IsLiveState(string state)
        => !string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "already exists", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(state, "skipped", StringComparison.OrdinalIgnoreCase);

    private (int Active, int Queued, int Completed, int Failed) CountKnownJobs(int liveJobCount)
    {
        var records = _knownJobs.Values.ToArray();
        if (records.Length == 0)
            return (liveJobCount, 0, 0, 0);

        int queued = 0;
        int active = 0;
        int completed = 0;
        int failed = 0;

        foreach (var record in records)
        {
            if (IsQueuedState(record.State))
                queued++;
            else if (IsSuccessfulTerminalState(record.State))
                completed++;
            else if (IsFailedTerminalState(record.State))
                failed++;
            else if (IsLiveState(record.State))
                active++;
        }

        return (Math.Max(active, liveJobCount), queued, completed, failed);
    }

    private static string BuildCountsMarkup((int Active, int Queued, int Completed, int Failed) counts)
    {
        var failedPart = counts.Failed > 0 ? $"[red]{counts.Failed}[/]" : $"{counts.Failed}";
        return $"[cyan]{counts.Active}[/] active, {counts.Queued} queued, [green]{counts.Completed}[/] completed, {failedPart} failed";
    }

    private static bool IsQueuedState(string state)
        => string.Equals(state, "pending", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "queued", StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessfulTerminalState(string state)
        => string.Equals(state, "done", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "succeeded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "alreadyexists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "already exists", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "skipped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "notfoundlasttime", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailedTerminalState(string state)
        => string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static int MaxLiveRows()
    {
        if (Console.IsOutputRedirected)
            return 0;

        try
        {
            return Math.Clamp(Console.WindowHeight - 8, 4, 30);
        }
        catch
        {
            return 12;
        }
    }

    private static string FormatJob(JobView job)
    {
        var suffix = job.Percent is int pct ? $" {pct}%" : "";
        if (job.TotalChildren is int total)
            suffix += $" [{job.DoneChildren ?? 0}/{total}]";

        return $"[{job.DisplayId}] {job.Kind}: {job.State}: {job.Name}{suffix}";
    }

    private static string FormatChild(JobChildView child)
    {
        var prefix = child.Percent is int pct ? $"({pct}%) " : "";
        return $"{prefix}{child.State}: {child.Name}";
    }

    private static string FormatLog(TerminalLogLine line)
        => $"[{line.DisplayId}] {line.JobType}: {line.Message}";
}

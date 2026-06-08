using Sockseek.Core.Models;

namespace Sockseek.Core.Jobs;

// JobOutcome describes the job state transition produced by execution.
// It should not carry post-commit side effects such as index updates,
// playlist writes, file organization, event emission, or on-complete hooks.
// Those belong in orchestration after the outcome is committed.
public sealed record JobOutcome
{
    public bool ShouldCommit { get; }
    public JobState State { get; }
    public FailureReason FailureReason { get; }
    public string? FailureMessage { get; }
    public string? DownloadPath { get; }
    public FileCandidate? ChosenCandidate { get; }

    private JobOutcome(
        bool shouldCommit,
        JobState state,
        FailureReason failureReason = FailureReason.None,
        string? failureMessage = null,
        string? downloadPath = null,
        FileCandidate? chosenCandidate = null)
    {
        ShouldCommit = shouldCommit;
        State = state;
        FailureReason = failureReason;
        FailureMessage = failureMessage;
        DownloadPath = downloadPath;
        ChosenCandidate = chosenCandidate;
    }

    public bool IsTerminal => State is JobState.Done
        or JobState.Failed
        or JobState.AlreadyExists
        or JobState.Skipped
        or JobState.NotFoundLastTime;

    public static JobOutcome NoChange()
        => new(shouldCommit: false, JobState.Pending);

    public static JobOutcome StateChange(JobState state)
        => new(shouldCommit: true, state);

    public static JobOutcome Done(string? downloadPath = null, FileCandidate? chosenCandidate = null)
        => new(shouldCommit: true, JobState.Done, downloadPath: downloadPath, chosenCandidate: chosenCandidate);

    public static JobOutcome Failed(FailureReason reason, string? message = null)
        => new(shouldCommit: true, JobState.Failed, reason, failureMessage: message);

    public static JobOutcome AlreadyExists(string? downloadPath = null)
        => new(shouldCommit: true, JobState.AlreadyExists, downloadPath: downloadPath);

    public static JobOutcome Skipped(JobState skipState, FailureReason reason = FailureReason.None)
        => skipState is JobState.Skipped or JobState.NotFoundLastTime
            ? new JobOutcome(shouldCommit: true, skipState, reason)
            : throw new ArgumentException("skipState must be Skipped or NotFoundLastTime.", nameof(skipState));

}

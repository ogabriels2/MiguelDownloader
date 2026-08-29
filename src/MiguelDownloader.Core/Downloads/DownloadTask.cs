using MiguelDownloader.Core.Errors;

namespace MiguelDownloader.Core.Downloads;

/// <summary>
/// A queued job and everything known about its run so far.
/// <para>
/// This is the persisted shape: the queue is written to the database on every state change, so
/// closing the app mid-download and reopening it restores the same list in the same order.
/// </para>
/// </summary>
public sealed class DownloadTask
{
    public required string Id { get; init; }
    public required DownloadRequest Request { get; init; }

    /// <summary>Position in the queue. Lower runs first.</summary>
    public int Order { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public DownloadStage Stage { get; set; } = DownloadStage.None;
    public DownloadProgress Progress { get; set; } = new();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Set once the job has produced a validated file.</summary>
    public string? ResultPath { get; set; }
    public long? ResultSizeBytes { get; set; }

    public DownloadError? Error { get; set; }

    /// <summary>Automatic retries already spent. Reset when the user retries by hand.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Working directory for this job, removed once it finishes cleanly.</summary>
    public string? WorkingDirectory { get; set; }

    public bool IsActive => Status is DownloadStatus.Running;
    public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Failed
        or DownloadStatus.Cancelled or DownloadStatus.Skipped;

    /// <summary>True when the user can meaningfully retry this job.</summary>
    public bool CanRetry => Status is DownloadStatus.Failed or DownloadStatus.Cancelled;

    public bool CanPause => Status is DownloadStatus.Running;
    public bool CanResume => Status is DownloadStatus.Paused;
    public bool CanCancel => Status is DownloadStatus.Running or DownloadStatus.Queued or DownloadStatus.Paused;

    public TimeSpan? Elapsed => StartedAt is null
        ? null
        : (FinishedAt ?? DateTimeOffset.Now) - StartedAt.Value;

    public string Title => Request.Item.Title;
}

namespace MiguelDownloader.Core.Downloads;

/// <summary>Where a queued item is in its life cycle.</summary>
public enum DownloadStatus
{
    /// <summary>In the queue, not yet started.</summary>
    Queued = 0,
    /// <summary>Actively working. <see cref="DownloadStage"/> says on what.</summary>
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
    /// <summary>Finished without downloading because the file was already there.</summary>
    Skipped,
}

/// <summary>
/// The step currently in progress. Reported separately from <see cref="DownloadStatus"/> so the
/// UI can say "converting" rather than just "running", which matters because post-processing can
/// take longer than the transfer itself.
/// </summary>
public enum DownloadStage
{
    None = 0,
    /// <summary>Fetching metadata and format list.</summary>
    Analyzing,
    DownloadingVideo,
    DownloadingAudio,
    DownloadingSubtitles,
    DownloadingThumbnail,
    /// <summary>Combining separate video and audio streams. No re-encoding.</summary>
    Muxing,
    /// <summary>Changing container without re-encoding.</summary>
    Remuxing,
    /// <summary>Re-encoding. The only lossy step, and only ever entered on request.</summary>
    Converting,
    WritingMetadata,
    EmbeddingCoverArt,
    /// <summary>Checking the finished file before declaring success.</summary>
    Validating,
    /// <summary>Moving from the working folder to the destination.</summary>
    Finalizing,
}

/// <summary>A point-in-time progress reading. Every field is optional because sources vary.</summary>
public sealed record DownloadProgress
{
    public DownloadStage Stage { get; init; }

    /// <summary>Bytes transferred for the current stream.</summary>
    public long? DownloadedBytes { get; init; }

    /// <summary>Total for the current stream, when the server reported one.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>True when <see cref="TotalBytes"/> is an estimate rather than a reported value.</summary>
    public bool TotalIsEstimate { get; init; }

    /// <summary>Bytes per second.</summary>
    public double? SpeedBytesPerSecond { get; init; }

    public TimeSpan? Eta { get; init; }

    /// <summary>Fragment counters for segmented transfers, when the downloader reports them.</summary>
    public int? FragmentIndex { get; init; }
    public int? FragmentCount { get; init; }

    /// <summary>
    /// Overall completion across all stages, 0..1. Null while the total is genuinely unknown,
    /// which the UI shows as an indeterminate bar rather than a made-up number.
    /// </summary>
    public double? OverallFraction { get; init; }

    /// <summary>Fraction of the current stream, 0..1, when the total is known.</summary>
    public double? StageFraction =>
        TotalBytes is > 0 && DownloadedBytes is >= 0
            ? Math.Clamp((double)DownloadedBytes.Value / TotalBytes.Value, 0, 1)
            : FragmentCount is > 0 && FragmentIndex is >= 0
                ? Math.Clamp((double)FragmentIndex.Value / FragmentCount.Value, 0, 1)
                : null;

    public static DownloadProgress ForStage(DownloadStage stage) => new() { Stage = stage };
}

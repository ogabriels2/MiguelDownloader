using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;

namespace MiguelDownloader.Engine.Downloads;

/// <summary>
/// Turns the downloader's output stream into progress updates.
/// <para>
/// A single job downloads video, then audio, then post-processes, and the tool reports each as a
/// separate transfer starting again from zero. Showing those raw percentages would make the bar
/// jump backwards, so this maps each stream onto a slice of the whole and reports one number that
/// only ever moves forward.
/// </para>
/// <para>
/// Instances are used by one job at a time, but the process layer raises output events on a
/// background thread, so the mutable fields are guarded.
/// </para>
/// </summary>
internal sealed class StageTracker(ResolvedFormats resolved, Action<DownloadProgress>? onProgress)
{
    private readonly ResolvedFormats _resolved = resolved;
    private readonly Action<DownloadProgress>? _onProgress = onProgress;
    private readonly object _gate = new();

    private readonly HashSet<string> _completedFormats = [];
    private DownloadStage _stage = DownloadStage.Analyzing;
    private double _lastReportedOverall;

    /// <summary>
    /// Weight of the transfer phase in the overall figure. Post-processing gets the remainder,
    /// because remuxing a long video is not instant and the bar should keep moving through it.
    /// </summary>
    private const double TransferShare = 0.90;

    /// <summary>Total number of streams this job downloads, used to slice up the transfer share.</summary>
    private int StreamCount => Math.Max(1, (_resolved.Video is null ? 0 : 1) + _resolved.Audio.Count);

    public void Consume(ProcessLine line)
    {
        if (!ProgressParser.TryParse(line.Text, out var update)) return;

        lock (_gate)
        {
            if (update.IsPostProcessing)
            {
                ConsumePostProcess(update);
                return;
            }

            ConsumeDownload(update);
        }
    }

    private void ConsumePostProcess(ProgressUpdate update)
    {
        var stage = ProgressParser.StageForPostProcessor(update.Postprocessor);
        if (stage == DownloadStage.None) return;

        _stage = stage;

        // Post-processing reports no byte counts, so the overall figure advances to the start of
        // the post-processing band and then waits rather than inventing motion.
        var overall = Math.Max(_lastReportedOverall, TransferShare);
        _lastReportedOverall = overall;

        _onProgress?.Invoke(new DownloadProgress
        {
            Stage = stage,
            OverallFraction = overall,
        });
    }

    private void ConsumeDownload(ProgressUpdate update)
    {
        var formatId = update.FormatId;
        _stage = StageForFormat(formatId);

        if (update.IsFinished && formatId is not null)
            _completedFormats.Add(formatId);

        var finished = _completedFormats.Count;
        var streams = StreamCount;

        // Each stream owns an equal slice of the transfer band. Within its slice, progress is the
        // fraction of that stream; slices already finished count as full.
        var sliceSize = TransferShare / streams;
        var currentFraction = update.TotalBytes is > 0 && update.DownloadedBytes is >= 0
            ? Math.Clamp((double)update.DownloadedBytes.Value / update.TotalBytes.Value, 0, 1)
            : update.FragmentCount is > 0 && update.FragmentIndex is >= 0
                ? Math.Clamp((double)update.FragmentIndex.Value / update.FragmentCount.Value, 0, 1)
                : 0;

        var completedSlices = Math.Min(finished, streams);
        var overall = (completedSlices * sliceSize) +
                      (completedSlices < streams ? currentFraction * sliceSize : 0);

        // Never let the reported figure go backwards, which would happen when the second stream
        // starts its own transfer from zero.
        overall = Math.Clamp(Math.Max(overall, _lastReportedOverall), 0, TransferShare);
        _lastReportedOverall = overall;

        _onProgress?.Invoke(new DownloadProgress
        {
            Stage = _stage,
            DownloadedBytes = update.DownloadedBytes,
            TotalBytes = update.TotalBytes,
            TotalIsEstimate = update.TotalIsEstimate,
            SpeedBytesPerSecond = update.SpeedBytesPerSecond,
            Eta = update.Eta,
            FragmentIndex = update.FragmentIndex,
            FragmentCount = update.FragmentCount,
            OverallFraction = overall,
        });
    }

    /// <summary>
    /// Works out whether the format currently transferring is the video or an audio track, so the
    /// UI can name the step instead of showing a generic "downloading".
    /// </summary>
    private DownloadStage StageForFormat(string? formatId)
    {
        if (formatId is null) return DownloadStage.DownloadingVideo;

        if (_resolved.Video is not null && _resolved.Video.FormatId == formatId)
            return DownloadStage.DownloadingVideo;

        if (_resolved.Audio.Any(a => a.FormatId == formatId))
            return DownloadStage.DownloadingAudio;

        // An id we did not pick usually means a subtitle or thumbnail fetch.
        return _resolved.Video is null ? DownloadStage.DownloadingAudio : DownloadStage.DownloadingVideo;
    }
}

using System.Globalization;
using MiguelDownloader.Core.Downloads;

namespace MiguelDownloader.Engine.YtDlp;

/// <summary>A parsed progress line from the downloader.</summary>
public sealed record ProgressUpdate
{
    /// <summary>"downloading", "finished" or "error" for transfers; "started"/"finished" for post-processing.</summary>
    public required string Status { get; init; }

    public long? DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public bool TotalIsEstimate { get; init; }
    public double? SpeedBytesPerSecond { get; init; }
    public TimeSpan? Eta { get; init; }
    public int? FragmentIndex { get; init; }
    public int? FragmentCount { get; init; }

    /// <summary>Which format this line refers to, letting the caller tell video from audio.</summary>
    public string? FormatId { get; init; }

    /// <summary>Post-processor name, on post-processing lines only.</summary>
    public string? Postprocessor { get; init; }

    public bool IsPostProcessing => Postprocessor is not null;
    public bool IsFinished => Status.Equals("finished", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the machine-readable progress lines emitted by <c>--progress-template</c>.
/// <para>
/// The template is prefixed with a sentinel so progress can be told apart from the extractor's
/// ordinary chatter on the same stream, and every field is separated by a character that cannot
/// occur inside the values. Unknown fields arrive as the literal <c>NA</c>, which is why each
/// value is parsed defensively rather than assumed present.
/// </para>
/// </summary>
public static class ProgressParser
{
    /// <summary>Marks a transfer progress line.</summary>
    public const string DownloadSentinel = "@MD-DL@";

    /// <summary>Marks a post-processing progress line.</summary>
    public const string PostProcessSentinel = "@MD-PP@";

    /// <summary>
    /// Field separator. A pipe cannot occur in any value the template interpolates
    /// (status, byte counts, speed, ETA, fragment indices, format id), so splitting is unambiguous
    /// and progress lines stay readable in the log.
    /// </summary>
    private const char Separator = '|';

    /// <summary>
    /// The <c>--progress-template</c> value for transfers. Field order must match
    /// <see cref="TryParse"/>.
    /// </summary>
    public static string DownloadTemplate =>
        "download:" + DownloadSentinel +
        "%(progress.status)s" + Separator +
        "%(progress.downloaded_bytes)s" + Separator +
        "%(progress.total_bytes)s" + Separator +
        "%(progress.total_bytes_estimate)s" + Separator +
        "%(progress.speed)s" + Separator +
        "%(progress.eta)s" + Separator +
        "%(progress.fragment_index)s" + Separator +
        "%(progress.fragment_count)s" + Separator +
        "%(info.format_id)s";

    /// <summary>The <c>--progress-template</c> value for post-processing.</summary>
    public static string PostProcessTemplate =>
        "postprocess:" + PostProcessSentinel +
        "%(progress.status)s" + Separator +
        "%(progress.postprocessor)s";

    /// <summary>
    /// Parses one output line. Returns false for anything that is not a progress line, which is
    /// most of what the downloader prints.
    /// </summary>
    public static bool TryParse(string? line, out ProgressUpdate update)
    {
        update = default!;
        if (string.IsNullOrEmpty(line)) return false;

        if (line.StartsWith(DownloadSentinel, StringComparison.Ordinal))
            return TryParseDownload(line[DownloadSentinel.Length..], out update);

        if (line.StartsWith(PostProcessSentinel, StringComparison.Ordinal))
            return TryParsePostProcess(line[PostProcessSentinel.Length..], out update);

        return false;
    }

    private static bool TryParseDownload(string payload, out ProgressUpdate update)
    {
        update = default!;
        var parts = payload.Split(Separator);
        if (parts.Length < 9) return false;

        var total = ParseLong(parts[2]);
        var estimate = ParseLong(parts[3]);

        update = new ProgressUpdate
        {
            Status = parts[0],
            DownloadedBytes = ParseLong(parts[1]),
            TotalBytes = total ?? estimate,
            TotalIsEstimate = total is null && estimate is not null,
            SpeedBytesPerSecond = ParseDouble(parts[4]),
            Eta = ParseEta(parts[5]),
            FragmentIndex = ParseInt(parts[6]),
            FragmentCount = ParseInt(parts[7]),
            FormatId = Clean(parts[8]),
        };
        return true;
    }

    private static bool TryParsePostProcess(string payload, out ProgressUpdate update)
    {
        update = default!;
        var parts = payload.Split(Separator);
        if (parts.Length < 2) return false;

        update = new ProgressUpdate
        {
            Status = parts[0],
            Postprocessor = Clean(parts[1]) ?? "unknown",
        };
        return true;
    }

    /// <summary>
    /// Maps a post-processor name onto the stage shown in the UI, so the user sees "converting"
    /// rather than an internal class name.
    /// </summary>
    public static DownloadStage StageForPostProcessor(string? postprocessor) => postprocessor switch
    {
        null => DownloadStage.None,
        var p when p.Contains("Merger", StringComparison.OrdinalIgnoreCase) => DownloadStage.Muxing,
        var p when p.Contains("Remux", StringComparison.OrdinalIgnoreCase) => DownloadStage.Remuxing,
        var p when p.Contains("VideoConvertor", StringComparison.OrdinalIgnoreCase) => DownloadStage.Converting,
        var p when p.Contains("ExtractAudio", StringComparison.OrdinalIgnoreCase) => DownloadStage.Converting,
        var p when p.Contains("Metadata", StringComparison.OrdinalIgnoreCase) => DownloadStage.WritingMetadata,
        var p when p.Contains("Thumbnail", StringComparison.OrdinalIgnoreCase) => DownloadStage.EmbeddingCoverArt,
        var p when p.Contains("SubtitlesConvertor", StringComparison.OrdinalIgnoreCase) => DownloadStage.DownloadingSubtitles,
        var p when p.Contains("EmbedSubtitle", StringComparison.OrdinalIgnoreCase) => DownloadStage.Muxing,
        var p when p.Contains("MoveFiles", StringComparison.OrdinalIgnoreCase) => DownloadStage.Finalizing,
        _ => DownloadStage.WritingMetadata,
    };

    /// <summary>
    /// yt-dlp writes "NA" for values it does not know, and occasionally an empty string.
    /// Both mean "no value", never zero.
    /// </summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("NA", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();

    private static long? ParseLong(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null) return null;
        // Sizes occasionally arrive with a fractional part.
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? (long)Math.Round(d)
            : null;
    }

    private static int? ParseInt(string? value)
    {
        var l = ParseLong(value);
        return l is null or > int.MaxValue ? null : (int)l.Value;
    }

    private static double? ParseDouble(string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is null) return null;
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d
            : null;
    }

    private static TimeSpan? ParseEta(string? value)
    {
        var seconds = ParseDouble(value);
        // An ETA beyond a day is noise from a stalled transfer, not information worth showing.
        return seconds is null or < 0 or > 86_400 ? null : TimeSpan.FromSeconds(seconds.Value);
    }
}

using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;

namespace MiguelDownloader.Core.Downloads;

/// <summary>Whether this job produces a video file or an audio file.</summary>
public enum DownloadMode
{
    Video = 0,
    Audio,
    /// <summary>Only the cover/thumbnail image.</summary>
    ThumbnailOnly,
    /// <summary>Only subtitle files.</summary>
    SubtitlesOnly,
}

/// <summary>Which subtitle tracks to fetch.</summary>
public sealed record SubtitleRequest
{
    public bool Enabled { get; init; }

    /// <summary>Language tags to fetch. Ignored when <see cref="AllLanguages"/> is set.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    public bool AllLanguages { get; init; }

    /// <summary>Include machine-generated captions alongside authored ones.</summary>
    public bool IncludeAutomatic { get; init; }

    public bool Embed { get; init; } = true;

    /// <summary>Also write the subtitle files next to the media.</summary>
    public bool KeepFiles { get; init; }

    public SubtitleFormatPreference Format { get; init; } = SubtitleFormatPreference.Srt;

    public static SubtitleRequest Disabled { get; } = new();
}

/// <summary>
/// A fully-resolved instruction to download one item.
/// <para>
/// Built once, before the job is queued, and treated as immutable afterwards. Everything the
/// pipeline needs is decided here, so a queued job survives a restart and behaves identically
/// when it resumes.
/// </para>
/// </summary>
public sealed record DownloadRequest
{
    public required string Url { get; init; }

    /// <summary>The analysed item. Kept so the queue can show a title and thumbnail immediately.</summary>
    public required MediaItem Item { get; init; }

    public DownloadMode Mode { get; init; } = DownloadMode.Video;

    /// <summary>Chosen format criteria. Resolved against the item at execution time.</summary>
    public FormatSelection Selection { get; init; } = new();

    /// <summary>Container for video output.</summary>
    public ContainerFormat Container { get; init; } = ContainerFormat.Auto;

    /// <summary>Audio output format for the audio flow.</summary>
    public AudioOutputFormat AudioFormat { get; init; } = AudioOutputFormat.KeepOriginal;

    /// <summary>Quality level for lossy audio conversion; ignored when keeping the original.</summary>
    public int LossyQuality { get; init; }

    public SubtitleRequest Subtitles { get; init; } = SubtitleRequest.Disabled;

    public bool EmbedThumbnail { get; init; }
    public bool WriteThumbnailFile { get; init; }
    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedChapters { get; init; } = true;

    /// <summary>Destination directory. Already expanded from any template.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>Final file name including extension. Already sanitised and collision-checked.</summary>
    public required string TargetFileName { get; init; }

    public ExistingFilePolicy ExistingFilePolicy { get; init; } = ExistingFilePolicy.Ask;

    /// <summary>Music tags to write. Null for non-music downloads.</summary>
    public MusicMetadata? Music { get; init; }

    /// <summary>Set when this item came from a playlist or album, for ordering and naming.</summary>
    public string? CollectionId { get; init; }
    public string? CollectionTitle { get; init; }
    public int? IndexInCollection { get; init; }

    /// <summary>Full path the job is expected to produce.</summary>
    public string TargetPath => Path.Combine(TargetDirectory, TargetFileName);
}

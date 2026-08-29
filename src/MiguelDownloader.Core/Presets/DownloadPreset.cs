using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Settings;

namespace MiguelDownloader.Core.Presets;

/// <summary>Whether a preset produces a video file or an audio file.</summary>
public enum PresetMode
{
    Video = 0,
    Audio,
}

/// <summary>
/// A saved combination of format, container and post-processing choices.
/// <para>
/// Built-in presets are immutable and always present; user presets are stored in settings.
/// A preset only ever expresses a <em>preference</em>: it is resolved against the formats an item
/// actually has, so a preset can never promise a quality the source does not carry.
/// </para>
/// </summary>
public sealed record DownloadPreset
{
    public required string Id { get; init; }

    /// <summary>Display name. For built-ins this is a resource key, resolved by the UI.</summary>
    public required string Name { get; init; }

    /// <summary>Short explanation of the trade-off, also a resource key for built-ins.</summary>
    public string Description { get; init; } = string.Empty;

    public PresetMode Mode { get; init; } = PresetMode.Video;

    /// <summary>True for the presets that ship with the app and cannot be edited or deleted.</summary>
    public bool IsBuiltIn { get; init; }

    // --- Video ---
    public QualityTier Tier { get; init; } = QualityTier.Best;
    public ContainerFormat Container { get; init; } = ContainerFormat.Auto;
    public VideoCodecPreference VideoCodec { get; init; } = VideoCodecPreference.Auto;
    public AudioCodecPreference AudioCodec { get; init; } = AudioCodecPreference.Auto;
    public int? MaxHeight { get; init; }
    public double? MaxFps { get; init; }

    /// <summary>What this preset optimises for when several formats fit.</summary>
    public FormatPriority Priority { get; init; } = FormatPriority.Quality;

    /// <summary>How this preset treats high dynamic range.</summary>
    public HdrPolicy Hdr { get; init; } = HdrPolicy.Auto;

    // --- Audio ---
    public AudioOutputFormat AudioFormat { get; init; } = AudioOutputFormat.KeepOriginal;
    public int LossyQuality { get; init; }

    // --- Extras ---
    public bool EmbedThumbnail { get; init; }
    public bool EmbedMetadata { get; init; } = true;
    public bool DownloadSubtitles { get; init; }
    public bool EmbedSubtitles { get; init; }

    /// <summary>Builds the selection criteria this preset stands for.</summary>
    public FormatSelection ToSelection() => new()
    {
        Tier = Tier,
        MaxHeight = MaxHeight,
        MaxFps = MaxFps,
        VideoCodec = VideoCodec,
        AudioCodec = AudioCodec,
        Priority = Priority,
        Hdr = Hdr,
    };
}

/// <summary>The built-in presets, and lookup across built-ins plus user presets.</summary>
public static class PresetCatalog
{
    public const string VideoBestId = "video.best";
    public const string VideoCompatibleId = "video.compatible";
    public const string VideoSmallId = "video.small";
    public const string MusicBestId = "music.best";
    public const string MusicMp3Id = "music.mp3";

    /// <summary>
    /// Highest quality the source has, with no re-encoding anywhere in the pipeline.
    /// The container is chosen to fit the streams rather than the other way round.
    /// </summary>
    public static DownloadPreset VideoBest { get; } = new()
    {
        Id = VideoBestId,
        Name = "Preset_VideoBest",
        Description = "Preset_VideoBest_Description",
        Mode = PresetMode.Video,
        IsBuiltIn = true,
        Tier = QualityTier.Best,
        Container = ContainerFormat.Auto,
        VideoCodec = VideoCodecPreference.Auto,
        Priority = FormatPriority.Quality,
        Hdr = HdrPolicy.PreferHdr,
        EmbedMetadata = true,
    };

    /// <summary>
    /// Aims for an MP4 that plays anywhere. H.264 and AAC are preferred so the result is a plain
    /// remux; if the source has no H.264 at the chosen quality, the pipeline reports the
    /// conversion before doing it rather than silently re-encoding.
    /// </summary>
    public static DownloadPreset VideoCompatible { get; } = new()
    {
        Id = VideoCompatibleId,
        Name = "Preset_VideoCompatible",
        Description = "Preset_VideoCompatible_Description",
        Mode = PresetMode.Video,
        IsBuiltIn = true,
        Tier = QualityTier.Best,
        Container = ContainerFormat.Mp4,
        VideoCodec = VideoCodecPreference.H264,
        AudioCodec = AudioCodecPreference.Aac,
        Priority = FormatPriority.Compatibility,
        // An HDR stream would need tone-mapping to reach an SDR H.264 target, which is a lossy
        // re-encode. This preset exists to avoid exactly that, so it asks for SDR up front.
        Hdr = HdrPolicy.PreferSdr,
        EmbedMetadata = true,
    };

    /// <summary>720p ceiling with efficient codecs, for when disk or bandwidth is the constraint.</summary>
    public static DownloadPreset VideoSmall { get; } = new()
    {
        Id = VideoSmallId,
        Name = "Preset_VideoSmall",
        Description = "Preset_VideoSmall_Description",
        Mode = PresetMode.Video,
        IsBuiltIn = true,
        Tier = QualityTier.Balanced,
        Container = ContainerFormat.Auto,
        VideoCodec = VideoCodecPreference.Auto,
        MaxHeight = 720,
        Priority = FormatPriority.Size,
        // HDR carries extra data for a file whose whole point is being small.
        Hdr = HdrPolicy.PreferSdr,
        EmbedMetadata = true,
    };

    /// <summary>
    /// Keeps the best audio stream exactly as published. No decode, no re-encode, so it is the
    /// only audio option that loses nothing at all.
    /// </summary>
    public static DownloadPreset MusicBest { get; } = new()
    {
        Id = MusicBestId,
        Name = "Preset_MusicBest",
        Description = "Preset_MusicBest_Description",
        Mode = PresetMode.Audio,
        IsBuiltIn = true,
        AudioFormat = AudioOutputFormat.KeepOriginal,
        AudioCodec = AudioCodecPreference.Auto,
        EmbedThumbnail = true,
        EmbedMetadata = true,
    };

    /// <summary>
    /// MP3 at the highest useful VBR setting, for players that accept nothing else. This is a
    /// second lossy pass over already-lossy audio, which the UI states plainly.
    /// </summary>
    public static DownloadPreset MusicMp3 { get; } = new()
    {
        Id = MusicMp3Id,
        Name = "Preset_MusicMp3",
        Description = "Preset_MusicMp3_Description",
        Mode = PresetMode.Audio,
        IsBuiltIn = true,
        AudioFormat = AudioOutputFormat.Mp3,
        LossyQuality = 0,
        EmbedThumbnail = true,
        EmbedMetadata = true,
    };

    public static IReadOnlyList<DownloadPreset> BuiltIn { get; } =
    [
        VideoBest, VideoCompatible, VideoSmall, MusicBest, MusicMp3,
    ];

    /// <summary>Finds a preset by id across built-ins and the user's own.</summary>
    public static DownloadPreset? Find(string? id, IEnumerable<DownloadPreset>? custom = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        var builtIn = BuiltIn.FirstOrDefault(p => p.Id == id);
        if (builtIn is not null) return builtIn;

        return custom?.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Built-ins followed by the user's presets, for display.</summary>
    public static IReadOnlyList<DownloadPreset> All(IEnumerable<DownloadPreset>? custom)
        => custom is null ? BuiltIn : [.. BuiltIn, .. custom];
}

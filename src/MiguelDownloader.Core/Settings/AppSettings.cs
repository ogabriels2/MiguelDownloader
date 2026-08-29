using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Naming;

namespace MiguelDownloader.Core.Settings;

/// <summary>What to do when the target file already exists.</summary>
public enum ExistingFilePolicy
{
    /// <summary>Stop and ask. The safe default: nothing is lost without a decision.</summary>
    Ask = 0,
    /// <summary>Add " (2)", " (3)"... and keep both.</summary>
    RenameAutomatically,
    /// <summary>Skip the download and treat the existing file as the result.</summary>
    Skip,
    /// <summary>Replace the existing file.</summary>
    Overwrite,
}

public enum AppTheme
{
    /// <summary>Follow the Windows app theme, and keep following it if the user changes it.</summary>
    System = 0,
    Light,
    Dark,
}

/// <summary>What the app does when a download finishes.</summary>
public enum CompletionAction
{
    Nothing = 0,
    OpenFolder,
    OpenFile,
}

/// <summary>Audio output formats offered by the music flow.</summary>
public enum AudioOutputFormat
{
    /// <summary>
    /// Keep the source stream exactly as published, in its own container. The only genuinely
    /// lossless option, because it does not decode the audio at all.
    /// </summary>
    KeepOriginal = 0,
    Mp3,
    M4a,
    Opus,
    Flac,
    Wav,
    Alac,
}

/// <summary>General preferences.</summary>
public sealed record GeneralSettings
{
    public string DownloadFolder { get; init; } = string.Empty;

    /// <summary>UI culture name, e.g. "pt-BR" or "en". Empty means follow the operating system.</summary>
    public string Language { get; init; } = string.Empty;

    public AppTheme Theme { get; init; } = AppTheme.System;
    public CompletionAction OnCompletion { get; init; } = CompletionAction.Nothing;
    public bool ShowNotifications { get; init; } = true;
    public bool ConfirmOnExitWithActiveDownloads { get; init; } = true;
    public bool WatchClipboard { get; init; }

    /// <summary>
    /// Check the stable GitHub release channel in the background, download a verified package,
    /// and apply it on the next safe restart.
    /// </summary>
    public bool AutomaticallyUpdateApplication { get; init; } = true;

    /// <summary>
    /// Rate-limits unattended checks so repeated launches do not consume GitHub's anonymous API
    /// allowance. Manual checks ignore this value.
    /// </summary>
    public DateTimeOffset? LastApplicationUpdateCheckUtc { get; init; }

    /// <summary>
    /// Whether the short guide has already been shown.
    /// <para>
    /// False on a fresh install, which is what makes the guide appear the first time and not
    /// again. It stays available from the Help menu, so this only decides whether it opens by
    /// itself.
    /// </para>
    /// </summary>
    public bool HasSeenWelcome { get; init; }
}

/// <summary>Defaults for the video flow.</summary>
public sealed record VideoSettings
{
    public QualityTier DefaultTier { get; init; } = QualityTier.Best;
    public ContainerFormat DefaultContainer { get; init; } = ContainerFormat.Auto;
    public VideoCodecPreference CodecPreference { get; init; } = VideoCodecPreference.Auto;
    public AudioCodecPreference AudioCodecPreference { get; init; } = AudioCodecPreference.Auto;

    /// <summary>
    /// How high dynamic range is treated by default. <see cref="HdrPolicy.Auto"/> preserves
    /// whatever the best stream happens to be rather than seeking or avoiding HDR.
    /// </summary>
    public HdrPolicy Hdr { get; init; } = HdrPolicy.Auto;

    /// <summary>Ceiling on frame rate. Null means take whatever the source offers.</summary>
    public double? MaxFps { get; init; }

    public bool EmbedThumbnail { get; init; }
    public bool EmbedMetadata { get; init; } = true;
    public bool EmbedChapters { get; init; } = true;

    public bool DownloadSubtitles { get; init; }
    public bool IncludeAutomaticSubtitles { get; init; }
    public bool EmbedSubtitles { get; init; } = true;
    public bool KeepSubtitleFiles { get; init; }
    public SubtitleFormatPreference SubtitleFormat { get; init; } = SubtitleFormatPreference.Srt;

    /// <summary>Subtitle languages to fetch. Empty means the UI asks each time.</summary>
    public IReadOnlyList<string> SubtitleLanguages { get; init; } = [];

    /// <summary>File-name template for standalone videos.</summary>
    public string FileNameTemplate { get; init; } = NameTemplate.Defaults.Video;

    /// <summary>File-name template for items downloaded as part of a playlist.</summary>
    public string PlaylistItemTemplate { get; init; } = NameTemplate.Defaults.PlaylistItem;

    /// <summary>Create a folder per playlist.</summary>
    public bool CreatePlaylistFolder { get; init; } = true;
}

/// <summary>Which subtitle file format to write.</summary>
public enum SubtitleFormatPreference
{
    /// <summary>Whatever the source provides, with no conversion at all.</summary>
    Original = 0,
    Srt,
    Vtt,
    Ass,
}

/// <summary>Defaults for the music flow.</summary>
public sealed record MusicSettings
{
    public AudioOutputFormat Format { get; init; } = AudioOutputFormat.KeepOriginal;

    /// <summary>
    /// Quality for lossy conversions. For MP3 this is a VBR quality level where 0 is best;
    /// ignored entirely when the source stream is kept as-is.
    /// </summary>
    public int LossyQuality { get; init; } = 0;

    public bool EmbedCoverArt { get; init; } = true;
    public bool WriteCoverArtFile { get; init; }
    public bool WriteMetadata { get; init; } = true;

    /// <summary>Lay albums out as Artist/Album/Track rather than a flat folder.</summary>
    public bool UseAlbumFolders { get; init; } = true;
    public bool IncludeYearInAlbumFolder { get; init; } = true;

    public string TrackTemplate { get; init; } = NameTemplate.Defaults.MusicTrack;
    public string SingleTemplate { get; init; } = NameTemplate.Defaults.MusicSingle;

    /// <summary>Root for music, when the user wants it separate from video downloads.</summary>
    public string MusicFolder { get; init; } = string.Empty;
    public bool UseSeparateMusicFolder { get; init; }
}

/// <summary>Queue and transfer behaviour.</summary>
public sealed record DownloadSettings
{
    /// <summary>
    /// Downloads running at once. Two is a deliberate default: it keeps a slow link busy without
    /// making progress reporting meaningless or thrashing the disk.
    /// </summary>
    public int MaxConcurrentDownloads { get; init; } = 2;

    /// <summary>Post-processing jobs allowed to run at once. Each can saturate several cores.</summary>
    public int MaxConcurrentProcessing { get; init; } = 1;

    /// <summary>Parallel fragment fetches within a single download.</summary>
    public int ConcurrentFragments { get; init; } = 4;

    public int RetryCount { get; init; } = 3;
    public bool ResumePartialDownloads { get; init; } = true;

    /// <summary>Transfer ceiling in kilobytes per second. Null means unlimited.</summary>
    public int? SpeedLimitKbps { get; init; }

    public ExistingFilePolicy ExistingFilePolicy { get; init; } = ExistingFilePolicy.Ask;

    /// <summary>
    /// Most items to take from one playlist, channel or profile. Zero means every one of them,
    /// which is the default.
    /// <para>
    /// A cap belongs to the person, not to the program: a playlist of a thousand tracks is an
    /// ordinary thing to paste, and quietly returning two hundred of them is worse than either
    /// taking the lot or saying plainly that it will not. The listing still arrives in pages so
    /// the first items appear at once, but it keeps going until the collection is complete.
    /// </para>
    /// </summary>
    public int MaxCollectionItems { get; init; }

    /// <summary>Working directory for partial files. Empty means a folder beside the target.</summary>
    public string TemporaryFolder { get; init; } = string.Empty;

    /// <summary>Warn before starting when free space looks insufficient.</summary>
    public bool CheckDiskSpace { get; init; } = true;

    /// <summary>Item count above which a bulk download asks for confirmation.</summary>
    public int BulkConfirmationThreshold { get; init; } = 20;
}

/// <summary>Paths and behaviour for the external tools, plus diagnostics.</summary>
public sealed record AdvancedSettings
{
    /// <summary>Override for yt-dlp. Empty means use the managed copy.</summary>
    public string YtDlpPath { get; init; } = string.Empty;

    /// <summary>Override for ffmpeg. Empty means use the managed copy.</summary>
    public string FfmpegPath { get; init; } = string.Empty;

    /// <summary>Override for ffprobe. Empty means use the managed copy.</summary>
    public string FfprobePath { get; init; } = string.Empty;

    /// <summary>
    /// JavaScript runtime for the extractor challenge solver. Empty means auto-detect.
    /// Without one, YouTube may publish fewer formats.
    /// </summary>
    public string JsRuntimePath { get; init; } = string.Empty;

    public bool AutoUpdateYtDlp { get; init; } = true;

    /// <summary>Days between update checks for the download engine.</summary>
    public int UpdateCheckIntervalDays { get; init; } = 3;

    public bool VerboseLogging { get; init; }

    /// <summary>
    /// Browser to read cookies from for content the user is entitled to but which requires a
    /// session. Empty means no cookies are used at all.
    /// </summary>
    public string CookiesFromBrowser { get; init; } = string.Empty;

    /// <summary>Path to a Netscape-format cookie file, as an alternative to reading a browser.</summary>
    public string CookiesFilePath { get; init; } = string.Empty;

    /// <summary>
    /// Extra yt-dlp arguments for advanced users. Parsed with full quoting rules and passed as
    /// separate argv entries, never concatenated into a shell command line.
    /// </summary>
    public string ExtraYtDlpArguments { get; init; } = string.Empty;
}

/// <summary>The full settings document, versioned so it can be migrated safely.</summary>
public sealed record AppSettings
{
    /// <summary>Schema version. Bumped whenever a migration becomes necessary.</summary>
    public int Version { get; init; } = 1;

    public GeneralSettings General { get; init; } = new();
    public VideoSettings Video { get; init; } = new();
    public MusicSettings Music { get; init; } = new();
    public DownloadSettings Downloads { get; init; } = new();
    public AdvancedSettings Advanced { get; init; } = new();

    /// <summary>User-defined presets, in display order.</summary>
    public IReadOnlyList<Presets.DownloadPreset> CustomPresets { get; init; } = [];

    /// <summary>Id of the preset used by the one-click download button.</summary>
    public string DefaultPresetId { get; init; } = Presets.PresetCatalog.VideoBestId;

    public const int CurrentVersion = 1;
}

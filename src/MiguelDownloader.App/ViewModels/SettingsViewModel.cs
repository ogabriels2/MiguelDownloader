using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.ViewModels;

/// <summary>
/// The settings page.
/// <para>
/// Properties are loaded from the stored settings, edited freely, and written back on save.
/// Nothing is persisted per keystroke, so an abandoned edit leaves the stored configuration
/// untouched.
/// </para>
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ToolService _tools;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly ShellService _shell;
    private readonly DiagnosticsService _diagnostics;
    private readonly AppUpdateService _appUpdates;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        SettingsService settings,
        ToolService tools,
        ThemeService theme,
        DialogService dialogs,
        ShellService shell,
        DiagnosticsService diagnostics,
        AppUpdateService appUpdates,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _tools = tools;
        _theme = theme;
        _dialogs = dialogs;
        _shell = shell;
        _diagnostics = diagnostics;
        _appUpdates = appUpdates;
        _logger = logger;

        Load();
        _tools.ToolsChanged += (_, paths) => ApplyToolStatus(paths);
    }

    // --- General ---------------------------------------------------------------------------

    [ObservableProperty] private string _downloadFolder = string.Empty;
    [ObservableProperty] private string _language = string.Empty;
    [ObservableProperty] private AppTheme _theme_ = AppTheme.System;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _confirmOnExit = true;
    [ObservableProperty] private bool _watchClipboard;
    [ObservableProperty] private bool _automaticallyUpdateApplication = true;
    [ObservableProperty] private string _applicationUpdateStatusText = string.Empty;

    /// <summary>True for the GitHub/Velopack channel; false when MSIX owns servicing.</summary>
    public bool InAppUpdatesAvailable => !DistributionInfo.UpdatesManagedExternally;

    /// <summary>Used by the settings page to explain Microsoft Store servicing.</summary>
    public bool PackageManagedUpdates => DistributionInfo.UpdatesManagedExternally;

    // --- Video -----------------------------------------------------------------------------

    [ObservableProperty] private QualityTier _defaultTier = QualityTier.Best;
    [ObservableProperty] private ContainerFormat _defaultContainer = ContainerFormat.Auto;
    [ObservableProperty] private VideoCodecPreference _codecPreference = VideoCodecPreference.Auto;
    [ObservableProperty] private HdrPolicy _hdr = HdrPolicy.Auto;
    [ObservableProperty] private bool _embedThumbnail;
    [ObservableProperty] private bool _embedMetadata = true;
    [ObservableProperty] private bool _embedChapters = true;
    [ObservableProperty] private bool _downloadSubtitles;
    [ObservableProperty] private bool _includeAutomaticSubtitles;
    [ObservableProperty] private bool _embedSubtitles = true;
    [ObservableProperty] private bool _keepSubtitleFiles;
    [ObservableProperty] private SubtitleFormatPreference _subtitleFormat = SubtitleFormatPreference.Srt;
    [ObservableProperty] private string _videoTemplate = string.Empty;
    [ObservableProperty] private string _playlistItemTemplate = string.Empty;
    [ObservableProperty] private bool _createPlaylistFolder = true;

    // --- Music -----------------------------------------------------------------------------

    [ObservableProperty] private AudioOutputFormat _musicFormat = AudioOutputFormat.KeepOriginal;
    [ObservableProperty] private bool _embedCoverArt = true;
    [ObservableProperty] private bool _writeMusicMetadata = true;
    [ObservableProperty] private bool _useAlbumFolders = true;
    [ObservableProperty] private bool _includeYearInAlbumFolder = true;
    [ObservableProperty] private string _trackTemplate = string.Empty;

    // --- Downloads -------------------------------------------------------------------------

    [ObservableProperty] private int _maxConcurrentDownloads = 2;
    [ObservableProperty] private int _concurrentFragments = 4;
    [ObservableProperty] private int _retryCount = 3;

    /// <summary>Most items to take from one collection. Zero means all of them.</summary>
    [ObservableProperty] private int _maxCollectionItems;
    [ObservableProperty] private int _speedLimitKbps;
    [ObservableProperty] private bool _resumeDownloads = true;
    [ObservableProperty] private bool _checkDiskSpace = true;
    [ObservableProperty] private ExistingFilePolicy _existingFilePolicy = ExistingFilePolicy.Ask;

    // --- Advanced --------------------------------------------------------------------------

    [ObservableProperty] private string _ytDlpPath = string.Empty;
    [ObservableProperty] private string _ffmpegPath = string.Empty;
    [ObservableProperty] private string _jsRuntimePath = string.Empty;
    [ObservableProperty] private bool _autoUpdate = true;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private string _cookiesFromBrowser = string.Empty;
    [ObservableProperty] private string _extraArguments = string.Empty;

    // --- Status ----------------------------------------------------------------------------

    [ObservableProperty] private string _toolStatusText = string.Empty;
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _savedMessage;

    public IReadOnlyList<AppTheme> ThemeChoices { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];

    public IReadOnlyList<QualityTier> TierChoices { get; } =
        [QualityTier.Best, QualityTier.High, QualityTier.Balanced, QualityTier.Smallest];

    public IReadOnlyList<ContainerFormat> ContainerChoices { get; } =
        [ContainerFormat.Auto, ContainerFormat.Mp4, ContainerFormat.Mkv, ContainerFormat.WebM];

    public IReadOnlyList<HdrPolicy> HdrChoices { get; } =
        [HdrPolicy.Auto, HdrPolicy.PreferHdr, HdrPolicy.PreferSdr];

    public IReadOnlyList<VideoCodecPreference> CodecChoices { get; } =
        [VideoCodecPreference.Auto, VideoCodecPreference.H264, VideoCodecPreference.Vp9, VideoCodecPreference.Av1];

    public IReadOnlyList<AudioOutputFormat> AudioFormatChoices { get; } =
    [
        AudioOutputFormat.KeepOriginal, AudioOutputFormat.Mp3, AudioOutputFormat.M4a,
        AudioOutputFormat.Opus, AudioOutputFormat.Flac, AudioOutputFormat.Wav,
    ];

    public IReadOnlyList<ExistingFilePolicy> ExistingFileChoices { get; } =
    [
        ExistingFilePolicy.Ask, ExistingFilePolicy.RenameAutomatically,
        ExistingFilePolicy.Skip, ExistingFilePolicy.Overwrite,
    ];

    public IReadOnlyList<SubtitleFormatPreference> SubtitleFormatChoices { get; } =
    [
        SubtitleFormatPreference.Srt, SubtitleFormatPreference.Vtt,
        SubtitleFormatPreference.Ass, SubtitleFormatPreference.Original,
    ];

    /// <summary>
    /// Browsers yt-dlp can read cookies from. An empty entry means no cookies are used at all,
    /// which is the default.
    /// </summary>
    public IReadOnlyList<string> CookieBrowserChoices { get; } =
        ["", "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "chromium"];

    public IReadOnlyList<(string Code, string DisplayName)> LanguageChoices { get; } =
        [("", Loc.Get("Settings_Language_System")), .. Loc.SupportedLanguages];

    private void Load()
    {
        var s = _settings.Current;

        DownloadFolder = _settings.ResolveDownloadFolder();
        Language = s.General.Language;
        Theme_ = s.General.Theme;
        ShowNotifications = s.General.ShowNotifications;
        ConfirmOnExit = s.General.ConfirmOnExitWithActiveDownloads;
        WatchClipboard = s.General.WatchClipboard;
        AutomaticallyUpdateApplication = s.General.AutomaticallyUpdateApplication;

        DefaultTier = s.Video.DefaultTier;
        DefaultContainer = s.Video.DefaultContainer;
        CodecPreference = s.Video.CodecPreference;
        Hdr = s.Video.Hdr;
        EmbedThumbnail = s.Video.EmbedThumbnail;
        EmbedMetadata = s.Video.EmbedMetadata;
        EmbedChapters = s.Video.EmbedChapters;
        DownloadSubtitles = s.Video.DownloadSubtitles;
        IncludeAutomaticSubtitles = s.Video.IncludeAutomaticSubtitles;
        EmbedSubtitles = s.Video.EmbedSubtitles;
        KeepSubtitleFiles = s.Video.KeepSubtitleFiles;
        SubtitleFormat = s.Video.SubtitleFormat;
        VideoTemplate = s.Video.FileNameTemplate;
        PlaylistItemTemplate = s.Video.PlaylistItemTemplate;
        CreatePlaylistFolder = s.Video.CreatePlaylistFolder;

        MusicFormat = s.Music.Format;
        EmbedCoverArt = s.Music.EmbedCoverArt;
        WriteMusicMetadata = s.Music.WriteMetadata;
        UseAlbumFolders = s.Music.UseAlbumFolders;
        IncludeYearInAlbumFolder = s.Music.IncludeYearInAlbumFolder;
        TrackTemplate = s.Music.TrackTemplate;

        MaxConcurrentDownloads = s.Downloads.MaxConcurrentDownloads;
        ConcurrentFragments = s.Downloads.ConcurrentFragments;
        RetryCount = s.Downloads.RetryCount;
        MaxCollectionItems = s.Downloads.MaxCollectionItems;
        SpeedLimitKbps = s.Downloads.SpeedLimitKbps ?? 0;
        ResumeDownloads = s.Downloads.ResumePartialDownloads;
        CheckDiskSpace = s.Downloads.CheckDiskSpace;
        ExistingFilePolicy = s.Downloads.ExistingFilePolicy;

        YtDlpPath = s.Advanced.YtDlpPath;
        FfmpegPath = s.Advanced.FfmpegPath;
        JsRuntimePath = s.Advanced.JsRuntimePath;
        AutoUpdate = s.Advanced.AutoUpdateYtDlp;
        VerboseLogging = s.Advanced.VerboseLogging;
        CookiesFromBrowser = s.Advanced.CookiesFromBrowser;
        ExtraArguments = s.Advanced.ExtraYtDlpArguments;

        if (PackageManagedUpdates)
        {
            AutomaticallyUpdateApplication = false;
            AutoUpdate = false;
            ApplicationUpdateStatusText = Loc.Get("Settings_StoreUpdatesManaged");
            UpdateStatusText = Loc.Get("Settings_StoreToolsManaged");
        }

        if (_tools.Current is { } paths) ApplyToolStatus(paths);
    }

    private void ApplyToolStatus(ToolPaths paths)
    {
        ToolStatusText = string.Join(Environment.NewLine,
            $"yt-dlp: {paths.YtDlp.Version ?? Loc.Get("Common_None")} ({paths.YtDlp.Source})",
            $"FFmpeg: {(paths.Ffmpeg.IsAvailable ? paths.Ffmpeg.Source.ToString() : Loc.Get("Common_None"))}",
            $"ffprobe: {(paths.Ffprobe.IsAvailable ? paths.Ffprobe.Source.ToString() : Loc.Get("Common_None"))}",
            $"JavaScript: {(paths.JsRuntime.IsAvailable ? paths.JsRuntime.Source.ToString() : Loc.Get("Common_None"))}");
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var current = _settings.Current;

        var updated = current with
        {
            General = current.General with
            {
                DownloadFolder = DownloadFolder,
                Language = Language,
                Theme = Theme_,
                ShowNotifications = ShowNotifications,
                ConfirmOnExitWithActiveDownloads = ConfirmOnExit,
                WatchClipboard = WatchClipboard,
                AutomaticallyUpdateApplication = InAppUpdatesAvailable && AutomaticallyUpdateApplication,
            },
            Video = current.Video with
            {
                DefaultTier = DefaultTier,
                DefaultContainer = DefaultContainer,
                CodecPreference = CodecPreference,
                Hdr = Hdr,
                EmbedThumbnail = EmbedThumbnail,
                EmbedMetadata = EmbedMetadata,
                EmbedChapters = EmbedChapters,
                DownloadSubtitles = DownloadSubtitles,
                IncludeAutomaticSubtitles = IncludeAutomaticSubtitles,
                EmbedSubtitles = EmbedSubtitles,
                KeepSubtitleFiles = KeepSubtitleFiles,
                SubtitleFormat = SubtitleFormat,
                FileNameTemplate = VideoTemplate,
                PlaylistItemTemplate = PlaylistItemTemplate,
                CreatePlaylistFolder = CreatePlaylistFolder,
            },
            Music = current.Music with
            {
                Format = MusicFormat,
                EmbedCoverArt = EmbedCoverArt,
                WriteMetadata = WriteMusicMetadata,
                UseAlbumFolders = UseAlbumFolders,
                IncludeYearInAlbumFolder = IncludeYearInAlbumFolder,
                TrackTemplate = TrackTemplate,
            },
            Downloads = current.Downloads with
            {
                MaxConcurrentDownloads = Math.Clamp(MaxConcurrentDownloads, 1, 8),
                ConcurrentFragments = Math.Clamp(ConcurrentFragments, 1, 16),
                RetryCount = Math.Clamp(RetryCount, 0, 10),
            MaxCollectionItems = Math.Max(0, MaxCollectionItems),
                SpeedLimitKbps = SpeedLimitKbps > 0 ? SpeedLimitKbps : null,
                ResumePartialDownloads = ResumeDownloads,
                CheckDiskSpace = CheckDiskSpace,
                ExistingFilePolicy = ExistingFilePolicy,
            },
            Advanced = current.Advanced with
            {
                YtDlpPath = YtDlpPath,
                FfmpegPath = FfmpegPath,
                JsRuntimePath = JsRuntimePath,
                AutoUpdateYtDlp = InAppUpdatesAvailable && AutoUpdate,
                VerboseLogging = VerboseLogging,
                CookiesFromBrowser = CookiesFromBrowser,
                ExtraYtDlpArguments = ExtraArguments,
            },
        };

        await _settings.SaveAsync(updated).ConfigureAwait(true);

        _theme.Apply(Theme_);
        await _tools.RefreshAsync().ConfigureAwait(true);

        SavedMessage = Loc.Get("Settings_Saved");
        _logger.LogInformation("Settings saved");
    }

    [RelayCommand]
    private void BrowseDownloadFolder()
    {
        var picked = _dialogs.PickFolder(DownloadFolder);
        if (picked is not null) DownloadFolder = picked;
    }

    [RelayCommand]
    private void BrowseYtDlp()
    {
        var picked = _dialogs.PickExecutable();
        if (picked is not null) YtDlpPath = picked;
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var picked = _dialogs.PickExecutable();
        if (picked is not null) FfmpegPath = picked;
    }

    [RelayCommand]
    private void OpenLogs() => _shell.OpenFolder(App.LogDirectory, createIfMissing: true);

    [RelayCommand]
    private void OpenDownloadFolder() => _shell.OpenFolder(DownloadFolder, createIfMissing: true);

    [RelayCommand]
    private async Task CheckApplicationUpdatesAsync()
    {
        if (!InAppUpdatesAvailable)
        {
            ApplicationUpdateStatusText = Loc.Get("Settings_StoreUpdatesManaged");
            return;
        }

        IsBusy = true;
        ApplicationUpdateStatusText = Loc.Get("Common_Loading");
        try
        {
            var result = await _appUpdates.CheckAndDownloadAsync(manual: true).ConfigureAwait(true);
            ApplicationUpdateStatusText = result.Status switch
            {
                AppUpdateStatus.UpToDate => Loc.Get("Settings_AppUpToDate"),
                AppUpdateStatus.ReadyToRestart => Loc.Format("Settings_AppUpdateReady", result.Version ?? string.Empty),
                AppUpdateStatus.NotInstalled => Loc.Get("Settings_AppUpdateNotInstalled"),
                AppUpdateStatus.ManagedExternally => Loc.Get("Settings_StoreUpdatesManaged"),
                AppUpdateStatus.Busy => Loc.Get("Settings_AppUpdateBusy"),
                AppUpdateStatus.Failed => Loc.Get("Settings_AppUpdateFailed"),
                _ => string.Empty,
            };
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (!InAppUpdatesAvailable)
        {
            UpdateStatusText = Loc.Get("Settings_StoreToolsManaged");
            return;
        }

        IsBusy = true;
        UpdateStatusText = Loc.Get("Common_Loading");
        try
        {
            var newer = await _tools.CheckForEngineUpdateAsync().ConfigureAwait(true);

            if (newer is null)
            {
                UpdateStatusText = Loc.Get("Settings_UpToDate");
                return;
            }

            UpdateStatusText = Loc.Format("Settings_UpdateAvailable", newer);

            if (!_dialogs.Confirm(Loc.Format("Settings_UpdateAvailable", newer), Loc.Get("Settings_CheckUpdates")))
                return;

            UpdateStatusText = Loc.Get("Settings_Updating");
            var ok = await _tools.UpdateEngineAsync().ConfigureAwait(true);

            UpdateStatusText = ok ? Loc.Get("Settings_UpToDate") : Loc.Get("Tools_InstallFailed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update check failed");
            UpdateStatusText = Loc.Get("Tools_InstallFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var target = _dialogs.PickSaveFile(
            $"migueldownloader-diagnostico-{DateTime.Now:yyyyMMdd-HHmm}.zip", "ZIP (*.zip)|*.zip");

        if (target is null) return;

        IsBusy = true;
        try
        {
            var ok = await _diagnostics.ExportAsync(target).ConfigureAwait(true);
            if (ok) _shell.OpenContainingFolder(target);
            else _dialogs.ShowMessage(Loc.Get("Error_Unknown"), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

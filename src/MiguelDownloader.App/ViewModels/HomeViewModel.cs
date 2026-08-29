using System.Collections.ObjectModel;
using System.Windows;
using MiguelDownloader.App.Converters;
using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Presets;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Catalog;
using MiguelDownloader.Engine.YtDlp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.ViewModels;

/// <summary>
/// The Home page: paste a URL, see what it is, choose how to download it.
/// <para>
/// Analysis always happens before any download so the user chooses from what genuinely exists.
/// Every option list here is built from the analysed formats, which is what keeps the interface
/// from offering a resolution, codec or audio track the source does not actually have.
/// </para>
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MediaAnalyzer _analyzer;
    private readonly CatalogService _catalogs;
    private readonly CatalogDownloadService _catalogDownloads;
    private readonly ToolService _tools;
    private readonly SettingsService _settings;
    private readonly DownloadPlanner _planner;
    private readonly QueueCoordinator _queue;
    private readonly DialogService _dialogs;
    private readonly NotificationService _notifications;
    private readonly ILogger<HomeViewModel> _logger;

    private CancellationTokenSource? _analysisCancellation;
    private bool _applyingPreset;
    private bool _targetDirectoryCustomized;

    /// <summary>The resolved catalogue, when the pasted link came from a platform that encrypts its audio.</summary>
    private MusicCatalog? _catalog;

    public HomeViewModel(
        MediaAnalyzer analyzer,
        CatalogService catalogs,
        CatalogDownloadService catalogDownloads,
        ToolService tools,
        SettingsService settings,
        DownloadPlanner planner,
        QueueCoordinator queue,
        DialogService dialogs,
        NotificationService notifications,
        ILogger<HomeViewModel> logger)
    {
        _analyzer = analyzer;
        _catalogs = catalogs;
        _catalogDownloads = catalogDownloads;
        _tools = tools;
        _settings = settings;
        _planner = planner;
        _queue = queue;
        _dialogs = dialogs;
        _notifications = notifications;
        _logger = logger;

        ApplyDefaults();
    }

    // --- Input -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyPropertyChangedFor(nameof(HasUrl))]
    [NotifyPropertyChangedFor(nameof(SourceName))]
    [NotifyPropertyChangedFor(nameof(HasSourceName))]
    [NotifyPropertyChangedFor(nameof(SignInHint))]
    [NotifyPropertyChangedFor(nameof(ShowSignInHint))]
    private string _url = string.Empty;

    /// <summary>
    /// The site the pasted link belongs to, or empty when it is not one we describe by name.
    /// Read from the URL text alone, so it appears as the link is pasted rather than after a request.
    /// </summary>
    public string SourceName => CurrentUrlInfo?.ProviderName ?? string.Empty;

    public bool HasSourceName => SourceName.Length > 0;

    /// <summary>
    /// Advice for the sites that mostly refuse anonymous requests, offered before the attempt
    /// instead of after the failure. It never blocks anything: plenty of public items on those
    /// sites work without a session, and asking is the only way to find out.
    /// </summary>
    public string SignInHint => ShowSignInHint
        ? string.Format(Loc.Get("Home_SignInHint"), SourceName)
        : string.Empty;

    /// <summary>False once cookies are configured, because the advice has been taken.</summary>
    public bool ShowSignInHint => CurrentUrlInfo is { SignInLikely: true }
        && string.IsNullOrWhiteSpace(_settings.Current.Advanced.CookiesFromBrowser)
        && string.IsNullOrWhiteSpace(_settings.Current.Advanced.CookiesFilePath);

    private MediaUrlInfo? CurrentUrlInfo
        => MediaUrlParser.TryParse(MediaUrlParser.ExtractFirstUrl(Url) ?? Url, out var info) ? info : null;

    /// <summary>
    /// The ceiling the user configured, or null for no ceiling at all, which is the default.
    /// </summary>
    private int? CollectionLimit
        => _settings.Current.Downloads.MaxCollectionItems is var max && max > 0 ? max : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string? _analysisError;

    [ObservableProperty]
    private string? _analysisErrorAdvice;

    // --- Result ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(IsCollection))]
    [NotifyPropertyChangedFor(nameof(IsSingleItem))]
    // Without this, the Download button stays disabled after a successful analysis: its
    // CanExecute reads Result, and nothing would tell the command that Result had changed.
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private AnalysisResult? _result;

    public bool HasResult => Result is not null;
    public bool IsCollection => Result?.IsCollection == true;
    public bool IsSingleItem => Result is { IsCollection: false };

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultAuthor = string.Empty;

    [ObservableProperty]
    private string _resultKind = string.Empty;

    [ObservableProperty]
    private string _resultDuration = string.Empty;

    [ObservableProperty]
    private string? _resultThumbnail;

    [ObservableProperty]
    private string _resultCountText = string.Empty;

    /// <summary>
    /// True when the first listing stopped short of the whole collection.
    /// <para>
    /// A channel with thousands of videos takes minutes to enumerate, so the first pass is
    /// capped and returns quickly. Rather than silently hiding the rest, the interface says so
    /// and offers to load everything.
    /// </para>
    /// </summary>
    [ObservableProperty]
    private bool _isCollectionTruncated;

    /// <summary>How many items the source says the collection has, when it reports a count.</summary>
    [ObservableProperty]
    private int _declaredCount;

    [ObservableProperty]
    private bool _isLoadingAll;

    public ObservableCollection<string> Warnings { get; } = [];

    // --- Options ---------------------------------------------------------------------------

    public ObservableCollection<PresetOptionViewModel> Presets { get; } = [];

    [ObservableProperty]
    private PresetOptionViewModel? _selectedPreset;

    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAudioMode))]
    [NotifyPropertyChangedFor(nameof(IsVideoMode))]
    private DownloadMode _mode = DownloadMode.Video;

    public bool IsAudioMode => Mode == DownloadMode.Audio;
    public bool IsVideoMode => Mode == DownloadMode.Video;

    [ObservableProperty]
    private bool _showAdvanced;

    /// <summary>Resolutions the source actually offers, highest first.</summary>
    public ObservableCollection<ResolutionOptionViewModel> Resolutions { get; } = [];

    /// <summary>
    /// True once the source has told us which resolutions exist.
    /// <para>
    /// A collection has none: the app lists a playlist, a channel or a profile without opening
    /// every item, so the renditions are not known yet and differ between items anyway. The
    /// interface says that in words rather than presenting an empty menu, which would be a
    /// control that does nothing.
    /// </para>
    /// </summary>
    public bool HasResolutions => Resolutions.Count > 0;

    [ObservableProperty]
    private ResolutionOptionViewModel? _selectedResolution;

    public ObservableCollection<AudioTrackOptionViewModel> AudioTracks { get; } = [];

    public ObservableCollection<SubtitleOptionViewModel> Subtitles { get; } = [];

    public ObservableCollection<FormatRowViewModel> AllFormats { get; } = [];

    public ObservableCollection<CollectionItemViewModel> CollectionItems { get; } = [];

    [ObservableProperty]
    private bool _hasMultipleAudioTracks;

    [ObservableProperty]
    private bool _hasSubtitles;

    [ObservableProperty]
    private bool _downloadSubtitles;

    [ObservableProperty]
    private bool _embedSubtitles = true;

    [ObservableProperty]
    private ContainerFormat _container = ContainerFormat.Auto;

    [ObservableProperty]
    private VideoCodecPreference _codecPreference = VideoCodecPreference.Auto;

    [ObservableProperty]
    private AudioOutputFormat _audioFormat = AudioOutputFormat.KeepOriginal;

    [ObservableProperty]
    private bool _embedThumbnail;

    /// <summary>
    /// How this download treats HDR. A three-way policy rather than a checkbox, because
    /// "leave it alone" and "actively avoid it" are different requests and only one of them
    /// should ever cost the user picture information.
    /// </summary>
    [ObservableProperty]
    private HdrPolicy _hdr = HdrPolicy.Auto;

    /// <summary>
    /// True only when the source publishes an HDR rendition. The HDR control stays hidden
    /// otherwise, so the interface never offers a choice that would have no effect.
    /// </summary>
    [ObservableProperty]
    private bool _hasHdr;

    /// <summary>
    /// What the selection optimises for. Ranking rules genuinely differ by intent, so this is a
    /// real choice rather than a cosmetic label over one fixed ordering.
    /// </summary>
    [ObservableProperty]
    private FormatPriority _priority = FormatPriority.Quality;

    public IReadOnlyList<FormatPriority> PriorityChoices { get; } =
        [FormatPriority.Quality, FormatPriority.Compatibility, FormatPriority.Size];

    public IReadOnlyList<HdrPolicy> HdrChoices { get; } =
        [HdrPolicy.Auto, HdrPolicy.PreferHdr, HdrPolicy.PreferSdr];

    /// <summary>
    /// Explains the consequences of the current choices: which container will be used, whether
    /// anything gets re-encoded, and what that costs. Recomputed whenever a choice changes.
    /// </summary>
    [ObservableProperty]
    private string _planSummary = string.Empty;

    [ObservableProperty]
    private bool _planHasWarning;

    [ObservableProperty]
    private string _estimatedSizeText = string.Empty;

    /// <summary>
    /// The qualification that must accompany the number: exact, estimated, a floor, or
    /// unknown. Never shown without it.
    /// </summary>
    [ObservableProperty]
    private string _estimatedSizeLabel = string.Empty;

    public IReadOnlyList<ContainerFormat> ContainerChoices { get; } =
        [ContainerFormat.Auto, ContainerFormat.Mp4, ContainerFormat.Mkv, ContainerFormat.WebM];

    public IReadOnlyList<VideoCodecPreference> CodecChoices { get; } =
        [VideoCodecPreference.Auto, VideoCodecPreference.H264, VideoCodecPreference.Vp9, VideoCodecPreference.Av1];

    public IReadOnlyList<AudioOutputFormat> AudioFormatChoices { get; } =
    [
        AudioOutputFormat.KeepOriginal, AudioOutputFormat.Mp3, AudioOutputFormat.M4a,
        AudioOutputFormat.Opus, AudioOutputFormat.Flac, AudioOutputFormat.Wav,
    ];

    // --- Commands --------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseTargetDirectory()
    {
        var selected = _dialogs.PickFolder(TargetDirectory);
        if (string.IsNullOrWhiteSpace(selected)) return;

        _targetDirectoryCustomized = true;
        TargetDirectory = selected;
    }

    /// <summary>
    /// Whether the field holds anything. The primary button reads this to decide whether it is
    /// still offering to fetch the link from the clipboard or is ready to analyse what is there.
    /// </summary>
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    private bool CanAnalyze => !IsAnalyzing && !string.IsNullOrWhiteSpace(Url);

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var input = MediaUrlParser.ExtractFirstUrl(Url) ?? Url;

        if (!MediaUrlParser.TryParse(input, out var urlInfo))
        {
            AnalysisError = Loc.Get("Error_InvalidUrl");
            AnalysisErrorAdvice = Loc.Get("Recommend_CheckUrl");
            return;
        }

        // Replacing an in-flight analysis must not leave the old one running.
        await CancelAnalysisAsync().ConfigureAwait(true);
        _analysisCancellation = new CancellationTokenSource();
        var token = _analysisCancellation.Token;

        IsAnalyzing = true;
        AnalysisError = null;
        AnalysisErrorAdvice = null;
        ClearResult();

        try
        {
            var tools = _tools.Current ?? await _tools.RefreshAsync(token).ConfigureAwait(true);

            if (!tools.CanDownload)
            {
                AnalysisError = Loc.Get("Error_YtDlpMissing");
                AnalysisErrorAdvice = Loc.Get("Recommend_InstallTools");
                return;
            }

            // A link from a platform that encrypts its audio names a release rather than pointing
            // at one. Its catalogue is read instead, and each recording is located elsewhere when
            // the download starts -- which is also why this stays quick.
            if (CatalogService.Handles(urlInfo))
            {
                // The whole playlist, unless the user asked for a ceiling. Deezer answers a
                // hundred at a time, so a thousand-track playlist is ten requests rather than one
                // truncated answer.
                var catalog = await _catalogs
                    .ResolveAsync(urlInfo, CollectionLimit, enrich: true, token)
                    .ConfigureAwait(true);

                if (catalog is null)
                {
                    AnalysisError = Loc.Get("Error_CatalogueUnavailable");
                    AnalysisErrorAdvice = Loc.Get("Recommend_CheckUrl");
                    return;
                }

                _catalog = catalog;
                ApplyResult(CatalogPresentation.ToAnalysisResult(catalog, urlInfo));
                return;
            }

            _catalog = null;

            // A large collection is listed with a cap first, so the result appears in seconds
            // instead of minutes. The rest follows on its own immediately afterwards.
            var limit = urlInfo.IsBulk ? MediaAnalyzer.LargeCollectionThreshold : (int?)null;

            var analysis = await _analyzer
                .AnalyzeAsync(urlInfo, tools, _settings.Current.Advanced, limit, token)
                .ConfigureAwait(true);

            ApplyResult(analysis);

            // The first pass is a way to show something quickly, not a decision about how much of
            // the playlist the user wanted. Leaving the remainder behind a button meant a
            // thousand-track playlist quietly became two hundred, so the rest is fetched
            // automatically -- up to the user's own ceiling, when they set one.
            if (IsCollectionTruncated && !token.IsCancellationRequested)
                _ = ContinueListingAsync(urlInfo, token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Analysis cancelled");
        }
        catch (AnalysisException ex)
        {
            AnalysisError = Loc.Get(ex.Error.MessageKey);
            AnalysisErrorAdvice = ex.Error.Action != Core.Errors.RecommendedAction.None
                ? Loc.ForEnum("Recommend", ex.Error.Action)
                : null;
            _logger.LogInformation("Analysis failed: {Kind}", ex.Error.Kind);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure during analysis");
            AnalysisError = Loc.Get("Error_Unknown");
            AnalysisErrorAdvice = Loc.Get("Recommend_ContactSupportWithLogs");
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private void PasteUrl()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            Url = MediaUrlParser.ExtractFirstUrl(text) ?? text.Trim();
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            _logger.LogDebug(ex, "Could not read the clipboard");
        }
    }

    /// <summary>
    /// Paste and analyse as one action. A link arrives on the clipboard and the next thing anyone
    /// wants is to see what is behind it, so making that two separate clicks buys nothing.
    /// </summary>
    [RelayCommand]
    private void PasteAndAnalyze()
    {
        PasteUrl();
        if (AnalyzeCommand.CanExecute(null)) AnalyzeCommand.Execute(null);
    }

    /// <summary>Fills the field from an external source (clipboard watcher, drag and drop).</summary>
    public void SetUrl(string url, bool analyzeImmediately = false)
    {
        Url = url;
        if (analyzeImmediately && AnalyzeCommand.CanExecute(null)) AnalyzeCommand.Execute(null);
    }

    private bool CanLoadAll => IsCollectionTruncated && !IsLoadingAll && !IsAnalyzing;

    /// <summary>
    /// Re-lists the collection with no cap, so every item becomes selectable.
    /// <para>
    /// Kept as an explicit action because enumerating a large channel takes minutes. Whatever was
    /// already selected is remembered and re-applied, so asking for the rest does not undo the
    /// choices already made.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadAll))]
    private async Task LoadAllItemsAsync()
    {
        if (Result?.Source is not { } urlInfo) return;

        // Remember what the user turned *off*. New items arrive selected by default, which is
        // what "load the rest" implies, so only the deliberate exclusions need restoring.
        var deselected = CollectionItems
            .Where(i => !i.IsSelected)
            .Select(i => i.Item.Id)
            .ToHashSet(StringComparer.Ordinal);

        await CancelAnalysisAsync().ConfigureAwait(true);
        _analysisCancellation = new CancellationTokenSource();
        var token = _analysisCancellation.Token;

        IsLoadingAll = true;
        try
        {
            var tools = _tools.Current ?? await _tools.RefreshAsync(token).ConfigureAwait(true);

            var analysis = await _analyzer
                .AnalyzeAsync(urlInfo, tools, _settings.Current.Advanced, listingLimit: null, token)
                .ConfigureAwait(true);

            ApplyResult(analysis);

            if (deselected.Count > 0)
            {
                foreach (var item in CollectionItems.Where(i => deselected.Contains(i.Item.Id)))
                    item.IsSelected = false;

                UpdateCollectionCount();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Full listing cancelled");
        }
        catch (AnalysisException ex)
        {
            AnalysisError = Loc.Get(ex.Error.MessageKey);
            _logger.LogInformation("Full listing failed: {Kind}", ex.Error.Kind);
        }
        finally
        {
            IsLoadingAll = false;
        }
    }

    [RelayCommand]
    private void SelectAllItems()
    {
        foreach (var item in CollectionItems) item.IsSelected = true;
        UpdateCollectionCount();
    }

    [RelayCommand]
    private void SelectNoItems()
    {
        foreach (var item in CollectionItems) item.IsSelected = false;
        UpdateCollectionCount();
    }

    private bool CanDownload => Result is not null && !IsAnalyzing;

    /// <summary>
    /// Fetches the rest of a collection after the quick first pass, in the background.
    /// <para>
    /// The selection is preserved across the replacement: anything the user had already unticked
    /// stays unticked, because the list growing underneath them must not undo their choices.
    /// </para>
    /// </summary>
    private async Task ContinueListingAsync(MediaUrlInfo urlInfo, CancellationToken token)
    {
        var deselected = CollectionItems.Where(i => !i.IsSelected).Select(i => i.Item.Id)
            .ToHashSet(StringComparer.Ordinal);

        IsLoadingAll = true;
        try
        {
            var tools = _tools.Current ?? await _tools.RefreshAsync(token).ConfigureAwait(true);

            var full = await _analyzer
                .AnalyzeAsync(urlInfo, tools, _settings.Current.Advanced, CollectionLimit, token)
                .ConfigureAwait(true);

            if (token.IsCancellationRequested) return;

            ApplyResult(full);

            if (deselected.Count > 0)
            {
                foreach (var item in CollectionItems.Where(i => deselected.Contains(i.Item.Id)))
                    item.IsSelected = false;

                UpdateCollectionCount();
            }

            _logger.LogInformation(
                "Listing completed: {Count} of {Declared} item(s)",
                CollectionItems.Count, DeclaredCount);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Background listing cancelled");
        }
        catch (Exception ex)
        {
            // The first pass is still on screen and still downloadable; failing to extend it is
            // a smaller problem than losing what is already there.
            _logger.LogWarning(ex, "Could not complete the listing; the first page remains usable");
        }
        finally
        {
            IsLoadingAll = false;
        }
    }

    /// <summary>
    /// Locates each selected recording and queues it as it is found.
    /// <para>
    /// Deliberately not awaited by the command: the point of the pipeline is that the first
    /// download starts within seconds while the rest of the release is still being looked for, and
    /// awaiting here would hold the interface until every track had been resolved.
    /// </para>
    /// </summary>
    private async Task QueueCatalogAsync(MusicCatalog catalog)
    {
        var chosen = CollectionItems.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) return;

        if (chosen.Count >= _settings.Current.Downloads.BulkConfirmationThreshold &&
            !_dialogs.ConfirmBulkDownload(chosen.Count))
        {
            return;
        }

        // The selection is by position, which is what ties a row back to its catalogue track.
        var selectedIds = chosen.Select(i => i.Item.Id).ToHashSet(StringComparer.Ordinal);
        var tracks = catalog.Tracks
            .Where(t => selectedIds.Contains(t.Isrc ?? $"{t.Artist}|{t.Title}"))
            .ToList();

        if (tracks.Count == 0) tracks = [.. catalog.Tracks];

        IsMatchingCatalogue = true;
        MatchedCount = 0;
        MatchTotal = tracks.Count;

        try
        {
            var directory = string.IsNullOrWhiteSpace(TargetDirectory)
                ? _settings.ResolveMusicFolder()
                : TargetDirectory;

            var results = await _catalogDownloads
                .QueueAsync(catalog, tracks, directory, OnTrackResolved)
                .ConfigureAwait(true);

            var missing = results.Count(r => !r.Queued);
            _notifications.Notify(
                Loc.Get("Nav_Downloads"),
                missing == 0
                    ? Loc.Get("Status_Queued")
                    : string.Format(Loc.Get("Catalogue_SomeNotFound"), missing),
                missing == 0 ? NotificationKind.Information : NotificationKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Queueing the catalogue failed");
            AnalysisError = Loc.Get("Error_Unknown");
        }
        finally
        {
            IsMatchingCatalogue = false;
        }
    }

    private void OnTrackResolved(CatalogQueueResult result)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            MatchedCount++;
            if (result is { Queued: true, Audio.Length: > 0 }) LosslessFound++;
        });
    }

    /// <summary>True while recordings are still being located; the queue is already running.</summary>
    [ObservableProperty]
    private bool _isMatchingCatalogue;

    [ObservableProperty]
    private int _matchedCount;

    [ObservableProperty]
    private int _matchTotal;

    /// <summary>How many were found on a source that serves them without loss.</summary>
    [ObservableProperty]
    private int _losslessFound;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private void Download()
    {
        if (Result is null) return;

        // A catalogue has no streams to plan against yet: each recording still has to be located,
        // which happens while the queue is already running rather than before it starts.
        if (_catalog is { } catalog)
        {
            _ = QueueCatalogAsync(catalog);
            return;
        }

        var choice = BuildChoice();

        try
        {
            if (Result.Collection is { } collection)
            {
                var selected = CollectionItems.Where(i => i.IsSelected).Select(i => i.Item).ToList();
                if (selected.Count == 0) return;

                if (selected.Count >= _settings.Current.Downloads.BulkConfirmationThreshold &&
                    !_dialogs.ConfirmBulkDownload(selected.Count))
                    return;

                var requests = _planner.PlanCollection(collection, selected, choice);
                _queue.Queue.EnqueueRange(requests.Select(CreateTask));
            }
            else if (Result.Item is { } item)
            {
                var request = _planner.PlanSingle(item, choice);
                _queue.Queue.Enqueue(CreateTask(request));
            }

            _notifications.Notify(
                Loc.Get("Nav_Downloads"), Loc.Get("Status_Queued"), NotificationKind.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not queue the download");
            _dialogs.ShowMessage(Loc.Get("Error_Unknown"), isError: true);
        }
    }

    private static DownloadTask CreateTask(DownloadRequest request)
        => new() { Id = Guid.NewGuid().ToString("N"), Request = request };

    private DownloadChoice BuildChoice()
    {
        var settings = _settings.Current;
        var preset = SelectedPreset?.Preset;

        var selection = new FormatSelection
        {
            Tier = SelectedResolution is null ? preset?.Tier ?? settings.Video.DefaultTier : QualityTier.Custom,
            MaxHeight = SelectedResolution?.Height ?? preset?.MaxHeight,
            MaxFps = preset?.MaxFps,
            VideoCodec = CodecPreference,
            AudioCodec = preset?.AudioCodec ?? AudioCodecPreference.Auto,
            Hdr = Hdr,
            Priority = Priority,
            AudioLanguages = AudioTracks.Where(t => t.IsSelected).Select(t => t.LanguageCode).ToList(),
        };

        var subtitleLanguages = Subtitles.Where(s => s.IsSelected).Select(s => s.LanguageCode).ToList();

        return new DownloadChoice
        {
            Mode = Mode,
            Selection = selection,
            Container = Container,
            AudioFormat = AudioFormat,
            LossyQuality = preset?.LossyQuality ?? settings.Music.LossyQuality,
            EmbedThumbnail = EmbedThumbnail || (Mode == DownloadMode.Audio && settings.Music.EmbedCoverArt),
            EmbedMetadata = preset?.EmbedMetadata ?? settings.Video.EmbedMetadata,
            EmbedChapters = settings.Video.EmbedChapters,
            TargetDirectoryOverride = string.IsNullOrWhiteSpace(TargetDirectory) ? null : TargetDirectory,
            Subtitles = DownloadSubtitles && subtitleLanguages.Count > 0
                ? new SubtitleRequest
                {
                    Enabled = true,
                    Languages = subtitleLanguages,
                    IncludeAutomatic = Subtitles
                        .Where(s => s.IsSelected)
                        .Any(s => s.Track.Origin != SubtitleOrigin.Manual),
                    Embed = EmbedSubtitles,
                    KeepFiles = settings.Video.KeepSubtitleFiles || !EmbedSubtitles,
                    Format = settings.Video.SubtitleFormat,
                }
                : SubtitleRequest.Disabled,
        };
    }

    // --- Result plumbing -------------------------------------------------------------------

    private void ApplyResult(AnalysisResult analysis)
    {
        Result = analysis;

        ResultTitle = analysis.Title;
        ResultKind = Loc.ForEnum("Kind", analysis.Kind);
        ResultThumbnail = analysis.Thumbnail?.Url;

        Warnings.Clear();
        foreach (var warning in analysis.Warnings) Warnings.Add(Loc.Get(warning));

        // The music flow is the default for music content, but the user can still switch.
        Mode = analysis.Profile == ContentProfile.Music ? DownloadMode.Audio : DownloadMode.Video;

        if (analysis.Collection is { } collection)
        {
            ResultAuthor = collection.AlbumArtist ?? collection.ChannelName ?? string.Empty;
            ResultDuration = collection.TotalDuration is { } total
                ? TimeSpanDisplayConverter.Format(total)
                : string.Empty;

            CollectionItems.Clear();
            var position = 1;
            foreach (var item in collection.Items)
                CollectionItems.Add(new CollectionItemViewModel(item, position++));

            // Say plainly when the listing stopped short, and offer the rest rather than
            // pretending the collection is only as big as what was fetched.
            IsCollectionTruncated = collection.IsTruncated;
            DeclaredCount = collection.DeclaredCount ?? collection.Count;
            LoadAllItemsCommand.NotifyCanExecuteChanged();

            UpdateCollectionCount();

            // A flat listing has no formats, so the per-format lists stay empty and the UI shows
            // the quality tiers instead of a resolution list that would be a guess.
            BuildFormatOptions(null);
        }
        else if (analysis.Item is { } item)
        {
            ResultAuthor = item.DisplayAuthor;
            ResultDuration = item.Duration is { } duration ? TimeSpanDisplayConverter.Format(duration) : string.Empty;
            ResultCountText = string.Empty;
            CollectionItems.Clear();

            BuildFormatOptions(item);
        }

        UpdatePlanSummary();
    }

    /// <summary>
    /// Builds the option lists from one item's real formats. With no item (a flat collection
    /// listing) the lists are cleared, and the simple quality tiers are used instead.
    /// </summary>
    private void BuildFormatOptions(MediaItem? item)
    {
        Resolutions.Clear();
        OnPropertyChanged(nameof(HasResolutions));
        AudioTracks.Clear();
        Subtitles.Clear();
        AllFormats.Clear();
        SelectedResolution = null;

        if (item is null)
        {
            HasMultipleAudioTracks = false;
            HasSubtitles = false;
            return;
        }

        var catalog = FormatCatalog.Build(item);

        foreach (var resolution in catalog.Resolutions)
            Resolutions.Add(new ResolutionOptionViewModel(resolution));

        foreach (var track in catalog.AudioTracks)
        {
            AudioTracks.Add(new AudioTrackOptionViewModel(track)
            {
                // The default track is pre-selected; extra dubs are opt-in.
                IsSelected = track.IsDefault,
            });
        }

        foreach (var subtitle in item.Subtitles.OrderBy(s => s.Origin).ThenBy(s => s.DisplayName))
            Subtitles.Add(new SubtitleOptionViewModel(subtitle));

        foreach (var format in item.SelectableFormats.OrderByDescending(f => f.Height ?? 0)
                     .ThenByDescending(f => f.TotalBitrate ?? 0))
            AllFormats.Add(new FormatRowViewModel(format));

        HasMultipleAudioTracks = catalog.HasMultipleAudioTracks;
        HasSubtitles = Subtitles.Count > 0;

        // A preset ceiling is resolved against the source rather than advertised as a promise.
        var maximum = SelectedPreset?.Preset.MaxHeight;
        SelectedResolution = maximum is { } height
            ? Resolutions.FirstOrDefault(r => r.Height <= height) ?? Resolutions.LastOrDefault()
            : Resolutions.FirstOrDefault();

        // Only meaningful when the source actually publishes an HDR rendition.
        HasHdr = catalog.HasHdr;
        Hdr = _settings.Current.Video.Hdr;
    }

    partial void OnSelectedResolutionChanged(ResolutionOptionViewModel? value) => UpdatePlanSummary();
    partial void OnContainerChanged(ContainerFormat value) => UpdatePlanSummary();
    partial void OnCodecPreferenceChanged(VideoCodecPreference value) => UpdatePlanSummary();
    partial void OnModeChanged(DownloadMode value)
    {
        if (!_targetDirectoryCustomized)
            TargetDirectory = value == DownloadMode.Audio
                ? _settings.ResolveMusicFolder()
                : _settings.ResolveDownloadFolder();

        UpdatePlanSummary();
    }
    partial void OnAudioFormatChanged(AudioOutputFormat value) => UpdatePlanSummary();
    partial void OnHdrChanged(HdrPolicy value) => UpdatePlanSummary();
    partial void OnPriorityChanged(FormatPriority value) => UpdatePlanSummary();
    partial void OnDownloadSubtitlesChanged(bool value) => UpdatePlanSummary();

    partial void OnSelectedPresetChanged(PresetOptionViewModel? value)
    {
        if (value is null || _applyingPreset) return;

        _applyingPreset = true;
        try
        {
            var preset = value.Preset;
            Mode = preset.Mode == PresetMode.Audio ? DownloadMode.Audio : DownloadMode.Video;
            Container = preset.Container;
            CodecPreference = preset.VideoCodec;
            Hdr = preset.Hdr;
            Priority = preset.Priority;
            AudioFormat = preset.AudioFormat;
            EmbedThumbnail = preset.EmbedThumbnail;
            DownloadSubtitles = preset.DownloadSubtitles;
            EmbedSubtitles = preset.EmbedSubtitles;

            if (Resolutions.Count > 0)
            {
                SelectedResolution = preset.MaxHeight is { } height
                    ? Resolutions.FirstOrDefault(r => r.Height <= height) ?? Resolutions.LastOrDefault()
                    : Resolutions.FirstOrDefault();
            }
        }
        finally
        {
            _applyingPreset = false;
        }

        UpdatePlanSummary();
    }

    /// <summary>
    /// Recomputes the human explanation of what will happen, using the same planner the download
    /// pipeline uses, so what the user is told matches what will actually be done.
    /// </summary>
    private void UpdatePlanSummary()
    {
        PlanHasWarning = false;
        EstimatedSizeText = string.Empty;
        EstimatedSizeLabel = string.Empty;

        var item = Result?.Item;
        if (item is null || item.Formats.Count == 0)
        {
            PlanSummary = string.Empty;
            return;
        }

        var choice = BuildChoice();

        if (Mode == DownloadMode.Audio)
        {
            var resolved = FormatSelector.ResolveAudioOnly(item, choice.Selection);
            var source = resolved.Audio.FirstOrDefault();

            if (source is null) { PlanSummary = string.Empty; return; }

            // Anything other than keeping the original stream re-encodes, and the encoder decides
            // the resulting size. What we know is how much will be downloaded.
            SetSize(resolved.Size, AudioFormat != AudioOutputFormat.KeepOriginal);

            // Converting already-lossy audio to a lossless container is a common misunderstanding,
            // so it is stated plainly rather than left for the user to assume.
            if (AudioFormat is AudioOutputFormat.Flac or AudioOutputFormat.Wav or AudioOutputFormat.Alac)
            {
                PlanSummary = Loc.Format("Warning_LossyToLossless", AudioFormat.ToString().ToUpperInvariant());
                PlanHasWarning = true;
                return;
            }

            PlanSummary = AudioFormat == AudioOutputFormat.KeepOriginal
                ? Loc.Get("MuxNote_NoReencodeNeeded")
                : Loc.Get("MuxNote_AudioTranscodeRequiredByContainer");
            PlanHasWarning = AudioFormat != AudioOutputFormat.KeepOriginal;
            return;
        }

        var video = FormatSelector.ResolveVideo(item, choice.Selection);
        if (!video.HasAnything) { PlanSummary = string.Empty; return; }

        // The plan is computed first because whether anything is re-encoded decides what the
        // size figure actually describes.
        var plan = MuxPlanner.Plan(
            video.Video, video.Audio, Container,
            choice.Subtitles.Enabled && choice.Subtitles.Embed,
            choice.EmbedThumbnail);

        SetSize(video.Size, plan.RequiresTranscode);

        var notes = plan.Notes.Select(n => Loc.ForEnum("MuxNote", n)).ToList();
        PlanSummary = string.Join(" ", notes);
        PlanHasWarning = plan.RequiresTranscode ||
                         plan.Notes.Contains(MuxNote.HdrAtRiskFromTranscode);
    }

    /// <summary>
    /// Shows the size together with the qualification it earned. The label comes from the
    /// estimate itself, so a floor can never be displayed as if it were a total.
    /// </summary>
    /// <param name="outputWillBeReencoded">
    /// True when the pipeline re-encodes, in which case the number describes what gets
    /// transferred, not the file that ends up on disk. A 2:38 track downloaded as a ~130 kbit/s
    /// stream and re-encoded to VBR MP3 arrives at roughly twice the size, so presenting the
    /// download figure as the file size would be wrong by a factor of two.
    /// </param>
    private void SetSize(SizeEstimate estimate, bool outputWillBeReencoded = false)
    {
        EstimatedSizeLabel = Loc.Get(estimate.LabelKeyFor(outputWillBeReencoded));

        if (!estimate.HasValue)
        {
            // "Unknown" is the whole message; there is no number to put beside it.
            EstimatedSizeText = string.Empty;
        EstimatedSizeLabel = string.Empty;
            return;
        }

        var formatted = ByteSizeConverter.Format(estimate.Bytes!.Value);
        EstimatedSizeText = estimate.Certainty == SizeCertainty.Estimated
            ? "~" + formatted
            : formatted;
    }

    private void UpdateCollectionCount()
    {
        var selected = CollectionItems.Count(i => i.IsSelected);
        ResultCountText = Loc.Format("Collection_AllSelected", selected, CollectionItems.Count);
    }

    private void ClearResult()
    {
        Result = null;
        ResultTitle = ResultAuthor = ResultKind = ResultDuration = ResultCountText = string.Empty;
        ResultThumbnail = null;
        Warnings.Clear();
        Resolutions.Clear();
        OnPropertyChanged(nameof(HasResolutions));
        AudioTracks.Clear();
        Subtitles.Clear();
        AllFormats.Clear();
        CollectionItems.Clear();
        PlanSummary = string.Empty;
        EstimatedSizeText = string.Empty;
        EstimatedSizeLabel = string.Empty;
    }

    private void ApplyDefaults()
    {
        var settings = _settings.Current;

        Presets.Clear();
        foreach (var preset in PresetCatalog.All(settings.CustomPresets))
            Presets.Add(new PresetOptionViewModel(preset));

        Container = settings.Video.DefaultContainer;
        CodecPreference = settings.Video.CodecPreference;
        Hdr = settings.Video.Hdr;
        AudioFormat = settings.Music.Format;
        EmbedThumbnail = settings.Video.EmbedThumbnail;
        DownloadSubtitles = settings.Video.DownloadSubtitles;
        EmbedSubtitles = settings.Video.EmbedSubtitles;
        TargetDirectory = _settings.ResolveDownloadFolder();
        SelectedPreset = Presets.FirstOrDefault(p => p.Preset.Id == settings.DefaultPresetId)
                         ?? Presets.FirstOrDefault();
    }

    private async Task CancelAnalysisAsync()
    {
        if (_analysisCancellation is null) return;

        try
        {
            await _analysisCancellation.CancelAsync().ConfigureAwait(true);
        }
        catch (ObjectDisposedException) { /* already finished */ }

        _analysisCancellation.Dispose();
        _analysisCancellation = null;
    }
}

using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.Engine.Dependencies;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.ViewModels;

/// <summary>Which page the shell is showing.</summary>
public enum ShellPage
{
    Home = 0,
    Downloads,
    History,
    Settings,
}

/// <summary>
/// The shell: navigation, first-run tool setup, and the messages shown across pages.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ToolService _tools;
    private readonly QueueCoordinator _queue;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly DialogService _dialogs;
    private readonly AppUpdateService _appUpdates;
    private readonly ILogger<MainViewModel> _logger;
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;

    public MainViewModel(
        ToolService tools,
        QueueCoordinator queue,
        SettingsService settings,
        NotificationService notifications,
        DialogService dialogs,
        AppUpdateService appUpdates,
        HomeViewModel home,
        DownloadsViewModel downloads,
        HistoryViewModel history,
        SettingsViewModel settingsPage,
        ILogger<MainViewModel> logger)
    {
        _tools = tools;
        _queue = queue;
        _settings = settings;
        _notifications = notifications;
        _dialogs = dialogs;
        _appUpdates = appUpdates;
        _logger = logger;

        Home = home;
        Downloads = downloads;
        History = history;
        Settings = settingsPage;

        // One second is fast enough to read as live and slow enough to cost nothing. The status
        // bar is the only consumer, so it stops mattering the moment the window is gone.
        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _statusTimer.Tick += (_, _) => RefreshStatusBar();
        _statusTimer.Start();

        History.DownloadAgainRequested += (_, url) =>
        {
            Home.SetUrl(url);
            CurrentPage = ShellPage.Home;
        };
    }

    public HomeViewModel Home { get; }
    public DownloadsViewModel Downloads { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private ShellPage _currentPage = ShellPage.Home;

    [ObservableProperty]
    private bool _isSettingUpTools;

    [ObservableProperty]
    private string _toolSetupMessage = string.Empty;

    [ObservableProperty]
    private double _toolSetupProgress;

    [ObservableProperty]
    private bool _toolSetupIndeterminate = true;

    [ObservableProperty]
    private bool _toolsMissing;

    /// <summary>Left-hand status text: either "Pronto" or the live queue counts.</summary>
    [ObservableProperty]
    private string _statusSummary = Loc.Get("Status_Ready");

    /// <summary>Combined rate of everything currently transferring.</summary>
    [ObservableProperty]
    private string _totalSpeedText = string.Empty;

    /// <summary>False when nothing is moving, which hides the rate rather than showing "0 B/s".</summary>
    [ObservableProperty]
    private bool _hasTransfer;

    /// <summary>Free space on the volume the downloads are going to.</summary>
    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    /// <summary>
    /// Prepares the application: resolves the tools, offers to install anything missing, then
    /// restores the queue.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var paths = await _tools.RefreshAsync().ConfigureAwait(true);

            if (!paths.IsComplete)
            {
                ToolsMissing = true;
                _logger.LogInformation("Missing tools: {Missing}", string.Join(", ", paths.MissingRequired));
            }

            await _queue.InitializeAsync().ConfigureAwait(true);

            // A background update check keeps the extractor current without blocking startup.
            _ = Task.Run(() => _tools.RunScheduledUpdateCheckAsync());

            // Application packages use a separate stable release channel. The check and download
            // are silent; a verified package is applied on the next safe restart.
            _ = _appUpdates.CheckAndDownloadAsync(manual: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup initialisation failed");
        }
    }

    /// <summary>Downloads and installs the missing tools, with progress.</summary>
    /// <summary>
    /// Recomputes the status bar: what the queue is doing, how fast it is going, and how much
    /// room is left where the files are landing.
    /// </summary>
    private void RefreshStatusBar()
    {
        var active = Downloads.ActiveCount;
        var pending = Downloads.PendingCount;

        StatusSummary = active == 0 && pending == 0
            ? Loc.Get("Status_Ready")
            : $"{active} {Loc.Get(active == 1 ? "Status_Active_One" : "Status_Active")}  ·  {pending} {Loc.Get("Status_Waiting")}";

        var speed = Downloads.Items.Sum(i => i.SpeedBytesPerSecond);
        HasTransfer = speed > 0;
        if (HasTransfer)
            TotalSpeedText = Converters.ByteSizeConverter.Format((long)speed) + "/s";

        RefreshFreeSpace();
    }

    /// <summary>
    /// Free space for the folder downloads are going to, re-read on every tick because the
    /// number falls as files land — which is exactly when someone looks at it.
    /// </summary>
    private void RefreshFreeSpace()
    {
        var target = Home.TargetDirectory;
        if (string.IsNullOrWhiteSpace(target))
            target = _settings.Current.General.DownloadFolder;
        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(target));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new System.IO.DriveInfo(root);
            if (!drive.IsReady) return;

            var free = Converters.ByteSizeConverter.Format(drive.AvailableFreeSpace);
            FreeSpaceText = $"{free} {string.Format(Loc.Get("Status_FreeSpace"), root.TrimEnd('\\'))}";
        }
        catch (Exception ex)
        {
            // A disconnected or unreadable volume is not worth interrupting anyone over.
            _logger.LogDebug(ex, "Could not read free space for {Target}", target);
            FreeSpaceText = string.Empty;
        }
    }
    [RelayCommand]
    private async Task InstallToolsAsync()
    {
        if (DistributionInfo.UpdatesManagedExternally)
        {
            ToolSetupMessage = Loc.Get("Settings_StoreToolsManaged");
            return;
        }

        IsSettingUpTools = true;
        ToolsMissing = false;
        ToolSetupIndeterminate = true;
        ToolSetupMessage = Loc.Get("Tools_Installing");

        var progress = new Progress<ToolInstallProgress>(report =>
        {
            ToolSetupMessage = $"{report.Tool} — {Loc.Get("Tools_Installing")}";

            if (report.Fraction is { } fraction)
            {
                ToolSetupIndeterminate = false;
                ToolSetupProgress = fraction * 100;
            }
            else
            {
                ToolSetupIndeterminate = true;
            }
        });

        try
        {
            var paths = await _tools.EnsureInstalledAsync(progress).ConfigureAwait(true);

            if (paths.IsComplete)
            {
                ToolSetupMessage = Loc.Get("Tools_Ready");
                _notifications.Notify(
                    Loc.Get("Settings_Tools"), Loc.Get("Tools_Ready"), NotificationKind.Success);

                // Anything queued before the tools arrived can start now.
                await _queue.Queue.StartAsync().ConfigureAwait(true);
            }
            else
            {
                ToolsMissing = true;
                ToolSetupMessage = Loc.Get("Tools_InstallFailed");
            }
        }
        catch (ToolVerificationException ex)
        {
            // A checksum mismatch means the download cannot be trusted; nothing is installed.
            _logger.LogError(ex, "Tool verification failed");
            ToolsMissing = true;
            _dialogs.ShowMessage(Loc.Get("Tools_InstallFailed"), isError: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool installation failed");
            ToolsMissing = true;
            _dialogs.ShowMessage(Loc.Get("Tools_InstallFailed"), isError: true);
        }
        finally
        {
            IsSettingUpTools = false;
        }
    }

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (Enum.TryParse<ShellPage>(page, ignoreCase: true, out var target))
            CurrentPage = target;
    }

    partial void OnCurrentPageChanged(ShellPage value)
    {
        // The history list is only worth querying when it is about to be shown.
        if (value == ShellPage.History) _ = History.RefreshAsync();
    }

    /// <summary>
    /// Decides whether the window may close, asking first when downloads are still running.
    /// </summary>
    public bool CanClose()
    {
        if (!_settings.Current.General.ConfirmOnExitWithActiveDownloads) return true;

        var active = _queue.Queue.ActiveCount;
        return active == 0 || _dialogs.ConfirmExitWithActiveDownloads(active);
    }

    /// <summary>Persists in-flight work so a restart can pick it up.</summary>
    public Task ShutdownAsync() => _queue.FlushAsync();
}

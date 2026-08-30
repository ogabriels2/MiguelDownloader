using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using MiguelDownloader.App.Localization;
using MiguelDownloader.App.Services;
using MiguelDownloader.App.ViewModels;
using MiguelDownloader.App.Views;
using MiguelDownloader.Data;
using MiguelDownloader.Engine.Catalog;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Downloads;
using MiguelDownloader.Engine.FFmpeg;
using MiguelDownloader.Engine.Files;
using MiguelDownloader.Engine.Music;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace MiguelDownloader.App;

/// <summary>
/// Application entry point: builds the service container, restores state, and shows the shell.
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _services;
    private ILogger<App>? _logger;

    /// <summary>Where logs, the database and settings live.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiguelDownloader");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>The container, for the few places that need to resolve late.</summary>
    public static IServiceProvider Services =>
        (Current as App)?._services
        ?? throw new InvalidOperationException("The service provider is not available yet.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ClaimSingleInstance())
        {
            // A second copy would open the same queue database and run a second queue coordinator
            // against it, which is how one download becomes two. The first window is brought
            // forward instead, because a person double-clicking the icon wants the program, not
            // an explanation.
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);

        ConfigureLogging();

        // Anything unhandled is logged and shown rather than closing the window silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _services = BuildServices();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Miguel Downloader starting");

        // The process identity must be set before any window exists, otherwise Windows associates
        // the notifications with the executable path instead of the installed shortcut.
        _services.GetRequiredService<NotificationService>().RegisterAppIdentity();

        var settings = _services.GetRequiredService<SettingsService>();
        settings.Load();

        // Create the database and apply any outstanding migrations before anything reads from it.
        // This has to happen up front: the queue restores itself during shell initialisation and
        // would otherwise find no tables.
        try
        {
            _services.GetRequiredService<AppDatabase>().Initialize();
        }
        catch (Exception ex)
        {
            // History and the persisted queue are conveniences; losing them must not stop the
            // application from starting and downloading.
            _logger.LogError(ex, "The database could not be initialised; history and the saved queue are unavailable");
        }

        Loc.SetLanguage(settings.Current.General.Language);

        _services.GetRequiredService<ThemeService>().Apply(settings.Current.General.Theme);

        try
        {
            var shell = _services.GetRequiredService<MainWindow>();
            shell.Show();
        }
        catch (Exception ex)
        {
            // A shell that cannot be built leaves nothing to interact with. Lingering as an
            // invisible process would look like the app simply failed to start, so this reports
            // what happened and exits rather than pretending to run.
            _logger.LogCritical(ex, "The main window could not be created");

            MessageBox.Show(
                $"{Loc.Get("Error_Unknown")}\n\n{ex.Message}\n\n{LogDirectory}",
                Loc.Get("App_Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDirectory, "migueldownloader-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                // Bounded so a runaway loop cannot fill the user's disk with log files.
                fileSizeLimitBytes: 16 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // --- Shared infrastructure ---
        services.AddSingleton(_ =>
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            // The GitHub API rejects requests that do not identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MiguelDownloader/1.0 (+https://github.com)");
            return client;
        });

        services.AddSingleton<ProcessRunner>();
        services.AddSingleton(provider => new ToolLocator(
            provider.GetRequiredService<ProcessRunner>(),
            provider.GetRequiredService<ILogger<ToolLocator>>(),
            allowManagedTools: !DistributionInfo.UpdatesManagedExternally,
            allowExternalTools: !DistributionInfo.UpdatesManagedExternally,
            useLgplTranscodeEncoders: DistributionInfo.UpdatesManagedExternally));
        services.AddSingleton<ToolInstaller>();
        services.AddSingleton<MediaAnalyzer>();
        services.AddSingleton<MediaProbe>();
        services.AddSingleton<WorkspaceManager>();
        services.AddSingleton<MusicTagger>();
        services.AddSingleton<DownloadExecutor>();
        services.AddSingleton<DownloadQueue>();

        // Reading a streaming platform's catalogue, and finding those recordings somewhere they
        // can actually be fetched. The platforms themselves encrypt their audio and none of it is
        // downloaded from them; see MusicPlatformInfo for what that means.
        services.AddSingleton<DeezerCatalogResolver>();
        services.AddSingleton<AppleMusicCatalogResolver>();
        services.AddSingleton<PublicPageReader>();
        services.AddSingleton<MusicBrainzEnricher>();
        services.AddSingleton<CatalogService>();
        services.AddSingleton<LosslessSearcher>();
        services.AddSingleton<TrackMatcher>();
        services.AddSingleton<CatalogMatchSession>();

        // --- Storage ---
        services.AddSingleton(provider => new AppDatabase(
            AppDatabase.DefaultPath,
            provider.GetRequiredService<ILogger<AppDatabase>>()));
        services.AddSingleton<HistoryRepository>();
        services.AddSingleton<QueueRepository>();
        services.AddSingleton(provider => new SettingsStore(
            SettingsStore.DefaultPath,
            provider.GetRequiredService<ILogger<SettingsStore>>()));

        // --- Application services ---
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<ToolService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<ClipboardWatcher>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<DownloadPlanner>();
        services.AddSingleton<CatalogDownloadService>();
        services.AddSingleton<QueueCoordinator>();
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<AppUpdateService>();

        // --- View models ---
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<DownloadsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // --- Views ---
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>The name of the per-session mutex that enforces one queue coordinator per user.</summary>
    private const string InstanceMutexName = "MiguelDownloader.SingleInstance";

    private static System.Threading.Mutex? _instanceMutex;

    /// <summary>
    /// Takes the single-instance mutex, or reports that another copy already holds it.
    /// </summary>
    private static bool ClaimSingleInstance()
    {
        // Local to the session on purpose: two people signed in at once are two independent users
        // with separate data directories, and neither should block the other.
        _instanceMutex = new System.Threading.Mutex(true, InstanceMutexName, out var createdNew);

        if (!createdNew)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        return createdNew;
    }

    /// <summary>Brings the window of the copy that is already running to the front.</summary>
    private static void ActivateRunningInstance()
    {
        try
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            var other = System.Diagnostics.Process.GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id && p.MainWindowHandle != IntPtr.Zero);

            if (other is null) return;

            NativeMethods.ShowWindow(other.MainWindowHandle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(other.MainWindowHandle);
        }
        catch (Exception ex)
        {
            // Failing to raise the other window is not worth blocking on: this copy is exiting
            // either way, and the first one is still there for the user to click.
            Log.Debug(ex, "Could not bring the running instance forward");
        }
    }

    private static class NativeMethods
    {
        internal const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Miguel Downloader exiting");

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        try
        {
            (_services as ServiceProvider)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failure while disposing services");
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled exception on the UI thread");

        MessageBox.Show(
            $"{Loc.Get("Error_Unknown")}\n\n{e.Exception.Message}",
            Loc.Get("App_Title"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keeping the window alive is better than vanishing mid-download; the queue is unaffected.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            _logger?.LogCritical(exception, "Unhandled exception on a background thread");

        Log.CloseAndFlush();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        // Already logged; leaving it unobserved would tear the process down on some configurations.
        e.SetObserved();
    }
}

using MiguelDownloader.App.Localization;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace MiguelDownloader.App.Services;

public enum AppUpdateStatus
{
    Skipped = 0,
    UpToDate,
    ReadyToRestart,
    NotInstalled,
    ManagedExternally,
    Busy,
    Failed,
}

public sealed record AppUpdateResult(AppUpdateStatus Status, string? Version = null);

/// <summary>
/// Keeps the application on the stable GitHub Releases channel. Velopack owns the transactional
/// download/apply machinery: packages are hash-checked, partial downloads are resumable, delta
/// failures fall back to a full package, and a process-wide lock prevents concurrent updates.
/// </summary>
public sealed class AppUpdateService(
    SettingsService settings,
    QueueCoordinator queue,
    NotificationService notifications,
    DialogService dialogs,
    ILogger<AppUpdateService> logger)
{
    public const string RepositoryUrl = "https://github.com/ogabriels2/MiguelDownloader";

    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    private readonly SettingsService _settings = settings;
    private readonly QueueCoordinator _queue = queue;
    private readonly NotificationService _notifications = notifications;
    private readonly DialogService _dialogs = dialogs;
    private readonly ILogger<AppUpdateService> _logger = logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <summary>
    /// Checks and downloads the newest stable release. Scheduled checks never interrupt active
    /// work; the verified package is applied automatically on the next launch. A manual check may
    /// offer an immediate restart when the queue is idle.
    /// </summary>
    public async Task<AppUpdateResult> CheckAndDownloadAsync(
        bool manual,
        CancellationToken cancellationToken = default)
    {
        if (DistributionInfo.UpdatesManagedExternally)
            return new AppUpdateResult(AppUpdateStatus.ManagedExternally);

        if (!manual && !ShouldRunAutomaticCheck())
            return new AppUpdateResult(AppUpdateStatus.Skipped);

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new AppUpdateResult(AppUpdateStatus.Busy);

        try
        {
            var manager = CreateManager();

            // Running from bin/Debug is deliberately not treated as an error. UpdateManager can
            // only make transactional replacements inside a real Velopack install/portable bundle.
            if (!manager.IsInstalled)
                return new AppUpdateResult(AppUpdateStatus.NotInstalled);

            if (manager.UpdatePendingRestart is { } pending)
            {
                var pendingVersion = pending.Version.ToString();
                if (manual)
                    await OfferRestartIfSafeAsync(manager, pending, pendingVersion).ConfigureAwait(false);

                return new AppUpdateResult(AppUpdateStatus.ReadyToRestart, pendingVersion);
            }

            UpdateInfo? update;
            try
            {
                update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            finally
            {
                // Record the attempt even when GitHub is temporarily unavailable. This prevents a
                // broken connection from generating a request on every launch.
                await RecordAutomaticCheckAsync().ConfigureAwait(false);
            }

            if (update is null)
                return new AppUpdateResult(AppUpdateStatus.UpToDate);

            var version = update.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Downloading application update {Version}", version);

            await manager.DownloadUpdatesAsync(
                update,
                progress => _logger.LogDebug("Application update download {Progress}%", progress),
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Application update {Version} is verified and ready", version);
            _notifications.Notify(
                Loc.Get("Update_Ready_Title"),
                Loc.Format("Update_Ready_Message", version),
                NotificationKind.Success);

            if (manual)
                await OfferRestartIfSafeAsync(manager, update.TargetFullRelease, version).ConfigureAwait(false);

            return new AppUpdateResult(AppUpdateStatus.ReadyToRestart, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (NotInstalledException)
        {
            return new AppUpdateResult(AppUpdateStatus.NotInstalled);
        }
        catch (ChecksumFailedException ex)
        {
            // Never apply an artifact that does not match the SHA-256 recorded in the release feed.
            _logger.LogError(ex, "Application update failed checksum verification");
            if (manual)
                _notifications.Notify(Loc.Get("Update_Failed_Title"), Loc.Get("Update_VerificationFailed"), NotificationKind.Error);
            return new AppUpdateResult(AppUpdateStatus.Failed);
        }
        catch (AcquireLockFailedException ex)
        {
            _logger.LogInformation(ex, "Another application update operation is already running");
            return new AppUpdateResult(AppUpdateStatus.Busy);
        }
        catch (Exception ex)
        {
            // Update failure must never make the downloader itself unavailable.
            _logger.LogWarning(ex, "Could not check or download an application update");
            if (manual)
                _notifications.Notify(Loc.Get("Update_Failed_Title"), Loc.Get("Update_Failed_Message"), NotificationKind.Warning);
            return new AppUpdateResult(AppUpdateStatus.Failed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static UpdateManager CreateManager()
        => new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    private bool ShouldRunAutomaticCheck()
    {
        var general = _settings.Current.General;
        if (!general.AutomaticallyUpdateApplication) return false;

        return general.LastApplicationUpdateCheckUtc is not { } last
            || DateTimeOffset.UtcNow - last >= AutomaticCheckInterval;
    }

    private async Task RecordAutomaticCheckAsync()
    {
        try
        {
            await _settings.UpdateAsync(current => current with
            {
                General = current.General with { LastApplicationUpdateCheckUtc = DateTimeOffset.UtcNow },
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist the application update check time");
        }
    }

    private async Task OfferRestartIfSafeAsync(
        UpdateManager manager,
        VelopackAsset pending,
        string version)
    {
        if (_queue.Queue.ActiveCount > 0)
        {
            _notifications.Notify(
                Loc.Get("Update_Ready_Title"),
                Loc.Get("Update_WaitsForDownloads"),
                NotificationKind.Information);
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        var restart = dispatcher.CheckAccess()
            ? _dialogs.Confirm(Loc.Format("Update_RestartPrompt", version), Loc.Get("Update_Ready_Title"))
            : await dispatcher.InvokeAsync(() =>
                _dialogs.Confirm(Loc.Format("Update_RestartPrompt", version), Loc.Get("Update_Ready_Title")));

        if (!restart)
            return;

        // Velopack exits the process immediately. Persist the queue before handing control over.
        await _queue.FlushAsync().ConfigureAwait(false);
        manager.ApplyUpdatesAndRestart(pending);
    }
}

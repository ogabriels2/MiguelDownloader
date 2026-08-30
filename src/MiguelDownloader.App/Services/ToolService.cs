using MiguelDownloader.Engine.Dependencies;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Keeps the resolved tool paths current and handles installing and updating them.
/// <para>
/// YouTube changes often, and yt-dlp ships fixes far more frequently than an application like
/// this would be rebuilt. Treating the download engine as a separately-updatable component is
/// what keeps the app working between its own releases.
/// </para>
/// </summary>
public sealed class ToolService(
    ToolLocator locator,
    ToolInstaller installer,
    SettingsService settings,
    ILogger<ToolService> logger)
{
    private readonly ToolLocator _locator = locator;
    private readonly ToolInstaller _installer = installer;
    private readonly SettingsService _settings = settings;
    private readonly ILogger<ToolService> _logger = logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The most recent resolution, or null before the first one.</summary>
    public ToolPaths? Current { get; private set; }

    public event EventHandler<ToolPaths>? ToolsChanged;

    /// <summary>Re-resolves every tool from the current settings.</summary>
    public async Task<ToolPaths> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await _locator
            .ResolveAsync(_settings.Current.Advanced, cancellationToken).ConfigureAwait(false);

        Current = resolved;

        _logger.LogInformation(
            "Tools resolved: yt-dlp={YtDlp} ({YtDlpSource}), ffmpeg={Ffmpeg}, ffprobe={Ffprobe}, js={Js}",
            resolved.YtDlp.Version ?? "missing", resolved.YtDlp.Source,
            resolved.Ffmpeg.IsAvailable, resolved.Ffprobe.IsAvailable, resolved.JsRuntime.Source);

        ToolsChanged?.Invoke(this, resolved);
        return resolved;
    }

    /// <summary>
    /// Installs whatever is missing. Safe to call when everything is already present: it simply
    /// re-resolves and returns.
    /// </summary>
    public async Task<ToolPaths> EnsureInstalledAsync(
        IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (DistributionInfo.UpdatesManagedExternally)
        {
            _logger.LogInformation("The package source manages installation of bundled tools");
            return Current ?? await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolved = Current ?? await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (resolved.IsComplete) return resolved;

            if (!resolved.YtDlp.IsAvailable)
                await _installer.InstallYtDlpAsync(progress, cancellationToken).ConfigureAwait(false);

            if (!resolved.Ffmpeg.IsAvailable || !resolved.Ffprobe.IsAvailable)
                await _installer.InstallFfmpegAsync(progress, cancellationToken).ConfigureAwait(false);

            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checks whether a newer yt-dlp exists, without installing it.</summary>
    public async Task<string?> CheckForEngineUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (DistributionInfo.UpdatesManagedExternally)
            return null;

        var latest = await _installer.GetLatestYtDlpVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latest is null) return null;

        var installed = Current?.YtDlp.Version;
        return ToolInstaller.IsNewer(latest, installed) ? latest : null;
    }

    /// <summary>
    /// Updates the download engine.
    /// <para>
    /// The previous binary is kept alongside the new one, so a release that turns out to be broken
    /// can be rolled back without a fresh download.
    /// </para>
    /// </summary>
    public async Task<bool> UpdateEngineAsync(
        IProgress<ToolInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (DistributionInfo.UpdatesManagedExternally)
        {
            _logger.LogInformation("The package source manages bundled tool updates");
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _installer.InstallYtDlpAsync(progress, cancellationToken).ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (ToolVerificationException ex)
        {
            // A checksum mismatch means the download is not trustworthy; the existing copy stays.
            _logger.LogError(ex, "Engine update rejected: the checksum did not match");
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Engine update failed");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Restores the previous engine binary after a failed update.
    /// </summary>
    public bool RollbackEngine()
    {
        var current = Path.Combine(ToolLocator.ManagedDirectory, "yt-dlp.exe");
        var backup = current + ".old";

        if (!File.Exists(backup))
        {
            _logger.LogWarning("No previous engine version is available to roll back to");
            return false;
        }

        try
        {
            File.Move(backup, current, overwrite: true);
            _logger.LogInformation("Rolled the download engine back to the previous version");
            _ = RefreshAsync();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not roll the engine back");
            return false;
        }
    }

    /// <summary>Runs the periodic update check when the interval has elapsed.</summary>
    public async Task RunScheduledUpdateCheckAsync(CancellationToken cancellationToken = default)
    {
        if (DistributionInfo.UpdatesManagedExternally)
            return;

        var advanced = _settings.Current.Advanced;
        if (!advanced.AutoUpdateYtDlp) return;

        var marker = Path.Combine(ToolLocator.ManagedDirectory, ".last-update-check");
        try
        {
            if (File.Exists(marker))
            {
                var last = File.GetLastWriteTimeUtc(marker);
                if (DateTime.UtcNow - last < TimeSpan.FromDays(Math.Max(1, advanced.UpdateCheckIntervalDays)))
                    return;
            }

            var newer = await CheckForEngineUpdateAsync(cancellationToken).ConfigureAwait(false);
            if (newer is not null)
            {
                _logger.LogInformation("Updating the download engine to {Version}", newer);
                await UpdateEngineAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            Directory.CreateDirectory(ToolLocator.ManagedDirectory);
            await File.WriteAllTextAsync(marker, DateTime.UtcNow.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            // A background update check must never interfere with using the application.
            _logger.LogWarning(ex, "The scheduled update check did not complete");
        }
    }
}

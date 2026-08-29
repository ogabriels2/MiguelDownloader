using MiguelDownloader.Core.Settings;
using MiguelDownloader.Data;
using Microsoft.Extensions.Logging;
using System.IO;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Holds the current settings and notifies the rest of the application when they change.
/// <para>
/// Everything reads settings through this one instance, so a change made on the settings page
/// takes effect immediately for the queue and the analyser without restarting.
/// </para>
/// </summary>
public sealed class SettingsService(SettingsStore store, ILogger<SettingsService> logger)
{
    private readonly SettingsStore _store = store;
    private readonly ILogger<SettingsService> _logger = logger;

    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after settings are replaced.</summary>
    public event EventHandler<AppSettings>? Changed;

    public void Load()
    {
        Current = _store.Load();
        _logger.LogInformation("Settings loaded from {Path}", _store.FilePath);
        Changed?.Invoke(this, Current);
    }

    /// <summary>Replaces the settings and persists them.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Current = settings;
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, Current);
    }

    /// <summary>Applies a targeted change without rebuilding the whole document by hand.</summary>
    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return SaveAsync(mutate(Current), cancellationToken);
    }

    /// <summary>The folder downloads go to, falling back if the configured one is unusable.</summary>
    public string ResolveDownloadFolder()
    {
        var configured = Current.General.DownloadFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Miguel Downloader");
    }

    /// <summary>The folder music goes to, which may be separate from video downloads.</summary>
    public string ResolveMusicFolder()
        => Current.Music.UseSeparateMusicFolder && !string.IsNullOrWhiteSpace(Current.Music.MusicFolder)
            ? Current.Music.MusicFolder
            : ResolveDownloadFolder();
}

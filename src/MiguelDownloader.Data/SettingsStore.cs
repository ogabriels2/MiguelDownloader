using System.Text.Json;
using System.Text.Json.Serialization;
using MiguelDownloader.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Data;

/// <summary>
/// Reads and writes the settings file.
/// <para>
/// Settings live in a readable JSON file rather than the database, so they can be inspected,
/// backed up or hand-edited. Writes go to a temporary file that is then swapped into place, which
/// means a crash mid-write cannot leave a truncated file that would reset every preference.
/// </para>
/// </summary>
public sealed class SettingsStore(string filePath, ILogger<SettingsStore> logger)
{
    private readonly string _filePath = filePath;
    private readonly ILogger<SettingsStore> _logger = logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MiguelDownloader", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string FilePath => _filePath;

    /// <summary>
    /// Loads the settings, falling back to defaults when the file is missing or unreadable.
    /// A corrupt file is moved aside rather than deleted, so nothing the user configured is lost
    /// without a copy remaining on disk.
    /// </summary>
    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogInformation("No settings file yet; starting from defaults");
            return WithDefaults(new AppSettings());
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);

            if (settings is null)
            {
                _logger.LogWarning("The settings file was empty; starting from defaults");
                return WithDefaults(new AppSettings());
            }

            return WithDefaults(Migrate(settings));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the settings file; starting from defaults");
            QuarantineCorruptFile();
            return WithDefaults(new AppSettings());
        }
    }

    /// <summary>Writes the settings atomically.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(settings, Options);

            // Write beside the target so the replace stays on the same volume and is atomic.
            var temporary = _filePath + ".tmp";
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);

            if (File.Exists(_filePath))
            {
                // Replace keeps the original in place until the swap succeeds.
                File.Replace(temporary, _filePath, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, _filePath);
            }

            _logger.LogDebug("Saved settings to {Path}", _filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference change is bad; crashing the app over it is worse.
            _logger.LogError(ex, "Could not save settings to {Path}", _filePath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Applies migrations for settings written by an older version. Runs before defaults are
    /// filled in, so a migration sees exactly what was stored.
    /// </summary>
    private AppSettings Migrate(AppSettings settings)
    {
        if (settings.Version >= AppSettings.CurrentVersion) return settings;

        _logger.LogInformation("Migrating settings from version {From} to {To}",
            settings.Version, AppSettings.CurrentVersion);

        // No migrations exist yet; version 1 is the first published schema. Future versions add
        // their transformations here, each guarded by the version it upgrades from.
        return settings with { Version = AppSettings.CurrentVersion };
    }

    /// <summary>
    /// Fills in values that can only be decided on the machine they run on, such as the default
    /// download folder.
    /// </summary>
    private static AppSettings WithDefaults(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.General.DownloadFolder)) return settings;

        var downloads = GetDefaultDownloadFolder();
        return settings with
        {
            General = settings.General with { DownloadFolder = downloads },
        };
    }

    /// <summary>
    /// Finds the user's Downloads folder. Windows exposes it as a known folder, but the
    /// Environment enumeration has no entry for it, so the registry is consulted first and the
    /// conventional path is used as a fallback.
    /// </summary>
    private static string GetDefaultDownloadFolder()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders";
                var value = Microsoft.Win32.Registry.GetValue(key, "{374DE290-123F-4565-9164-39C4925E467B}", null);
                if (value is string path && Directory.Exists(path))
                    return Path.Combine(path, "Miguel Downloader");
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
                                       or UnauthorizedAccessException)
        {
            // Fall through to the conventional location.
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "Downloads", "Miguel Downloader");
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(_filePath, backup);
            _logger.LogInformation("Moved the unreadable settings file to {Path}", backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not move the unreadable settings file aside");
        }
    }
}

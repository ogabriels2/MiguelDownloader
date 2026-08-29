using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Dependencies;

/// <summary>Progress while fetching a tool.</summary>
/// <param name="Tool">Which tool is being installed.</param>
/// <param name="Phase">What is happening right now.</param>
/// <param name="BytesReceived">Bytes downloaded so far.</param>
/// <param name="TotalBytes">Total size, when the server reported one.</param>
public readonly record struct ToolInstallProgress(
    ExternalTool Tool,
    ToolInstallPhase Phase,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : null;
}

public enum ToolInstallPhase
{
    CheckingVersion = 0,
    Downloading,
    Verifying,
    Extracting,
    Installing,
    Done,
}

/// <summary>Raised when a downloaded file does not match its published checksum.</summary>
public sealed class ToolVerificationException(string message) : Exception(message);

/// <summary>
/// Downloads and installs yt-dlp and ffmpeg.
/// <para>
/// Everything is fetched over HTTPS from the projects' own release channels, and nothing is
/// installed until its SHA-256 matches the checksum file published alongside it. A mismatch
/// aborts the install rather than falling back to running the binary anyway, because an
/// unverified executable is exactly the thing not worth being relaxed about.
/// </para>
/// </summary>
public sealed class ToolInstaller(HttpClient httpClient, ILogger<ToolInstaller> logger)
{
    private readonly HttpClient _http = httpClient;
    private readonly ILogger<ToolInstaller> _logger = logger;

    private const string YtDlpLatestApi = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";

    /// <summary>
    /// yt-dlp maintains its own ffmpeg builds, patched for the issues that affect it. Using them
    /// avoids a class of muxing bugs that generic builds still carry.
    /// </summary>
    private const string FfmpegLatestApi = "https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/latest";

    /// <summary>Fetches the latest published yt-dlp version without installing anything.</summary>
    public async Task<string?> GetLatestYtDlpVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetJsonAsync(YtDlpLatestApi, cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not check the latest yt-dlp version");
            return null;
        }
    }

    /// <summary>
    /// Installs or updates yt-dlp into the managed directory.
    /// </summary>
    /// <returns>The path to the installed executable.</returns>
    public async Task<string> InstallYtDlpAsync(
        IProgress<ToolInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ToolInstallProgress(ExternalTool.YtDlp, ToolInstallPhase.CheckingVersion));

        using var release = await GetJsonAsync(YtDlpLatestApi, cancellationToken).ConfigureAwait(false);
        var root = release.RootElement;

        var assetUrl = FindAssetUrl(root, "yt-dlp.exe")
            ?? throw new InvalidOperationException("The yt-dlp release does not publish yt-dlp.exe.");
        var sumsUrl = FindAssetUrl(root, "SHA2-256SUMS")
            ?? throw new InvalidOperationException("The yt-dlp release does not publish SHA2-256SUMS.");

        Directory.CreateDirectory(ToolLocator.ManagedDirectory);

        var tempFile = Path.Combine(ToolLocator.ManagedDirectory, $"yt-dlp.{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadAsync(assetUrl, tempFile, ExternalTool.YtDlp, progress, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress(ExternalTool.YtDlp, ToolInstallPhase.Verifying));

            var expected = await FetchChecksumAsync(sumsUrl, "yt-dlp.exe", cancellationToken).ConfigureAwait(false)
                ?? throw new ToolVerificationException("No published checksum was found for yt-dlp.exe.");

            await VerifyAsync(tempFile, expected, "yt-dlp.exe", cancellationToken).ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress(ExternalTool.YtDlp, ToolInstallPhase.Installing));

            var target = Path.Combine(ToolLocator.ManagedDirectory, "yt-dlp.exe");
            ReplaceFile(tempFile, target);

            _logger.LogInformation("Installed yt-dlp {Version}", root.TryGetProperty("tag_name", out var t)
                ? t.GetString() : "unknown");

            progress?.Report(new ToolInstallProgress(ExternalTool.YtDlp, ToolInstallPhase.Done));
            return target;
        }
        finally
        {
            TryDelete(tempFile);
        }
    }

    /// <summary>
    /// Installs ffmpeg and ffprobe into the managed directory.
    /// <para>
    /// The shared build is used rather than the static one: it is roughly half the download and,
    /// once the DLLs sit beside the executables, behaves identically.
    /// </para>
    /// </summary>
    public async Task<(string Ffmpeg, string Ffprobe)> InstallFfmpegAsync(
        IProgress<ToolInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ToolInstallProgress(ExternalTool.Ffmpeg, ToolInstallPhase.CheckingVersion));

        using var release = await GetJsonAsync(FfmpegLatestApi, cancellationToken).ConfigureAwait(false);
        var root = release.RootElement;

        const string assetName = "ffmpeg-master-latest-win64-gpl-shared.zip";
        var assetUrl = FindAssetUrl(root, assetName)
            ?? throw new InvalidOperationException($"The ffmpeg release does not publish {assetName}.");
        var sumsUrl = FindAssetUrl(root, "checksums.sha256")
            ?? throw new ToolVerificationException("The FFmpeg release does not publish checksums.sha256.");

        Directory.CreateDirectory(ToolLocator.ManagedDirectory);

        var tempZip = Path.Combine(Path.GetTempPath(), $"migueldownloader-ffmpeg.{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadAsync(assetUrl, tempZip, ExternalTool.Ffmpeg, progress, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress(ExternalTool.Ffmpeg, ToolInstallPhase.Verifying));
            var expected = await FetchChecksumAsync(sumsUrl, assetName, cancellationToken).ConfigureAwait(false)
                ?? throw new ToolVerificationException($"No published checksum was found for {assetName}.");
            await VerifyAsync(tempZip, expected, assetName, cancellationToken).ConfigureAwait(false);

            progress?.Report(new ToolInstallProgress(ExternalTool.Ffmpeg, ToolInstallPhase.Extracting));
            ExtractFfmpeg(tempZip);

            progress?.Report(new ToolInstallProgress(ExternalTool.Ffmpeg, ToolInstallPhase.Done));

            return (Path.Combine(ToolLocator.ManagedDirectory, "ffmpeg.exe"),
                    Path.Combine(ToolLocator.ManagedDirectory, "ffprobe.exe"));
        }
        finally
        {
            TryDelete(tempZip);
        }
    }

    /// <summary>
    /// Pulls ffmpeg.exe, ffprobe.exe and the shared libraries out of the archive, flattening the
    /// versioned top-level folder the build uses.
    /// </summary>
    private void ExtractFfmpeg(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var wanted = new[] { "ffmpeg.exe", "ffprobe.exe" };
        var extracted = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;

            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0) continue;

            // The bin folder holds the executables plus the DLLs they need.
            var isWanted = wanted.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                           (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                            entry.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase));

            if (!isWanted) continue;

            var target = Path.Combine(ToolLocator.ManagedDirectory, name);

            // Guard against an archive entry trying to escape the target directory.
            var fullTarget = Path.GetFullPath(target);
            var fullRoot = Path.GetFullPath(ToolLocator.ManagedDirectory);
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping archive entry with suspicious path: {Entry}", entry.FullName);
                continue;
            }

            entry.ExtractToFile(fullTarget, overwrite: true);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidOperationException("The ffmpeg archive did not contain the expected executables.");

        _logger.LogInformation("Extracted {Count} ffmpeg files", extracted);
    }

    private static string? FindAssetUrl(JsonElement release, string assetName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)) continue;
            if (!string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase)) continue;
            return asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null;
        }
        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a checksum file and returns the hash recorded for one file name.</summary>
    private async Task<string?> FetchChecksumAsync(
        string sumsUrl, string fileName, CancellationToken cancellationToken)
    {
        var text = await _http.GetStringAsync(sumsUrl, cancellationToken).ConfigureAwait(false);

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Format is "<hash>  <name>", occasionally with a leading '*' marking binary mode.
            var trimmed = line.Trim();
            var separator = trimmed.LastIndexOf(' ');
            if (separator <= 0) continue;

            var name = trimmed[(separator + 1)..].TrimStart('*', ' ');
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;

            return trimmed[..separator].Trim();
        }
        return null;
    }

    private async Task VerifyAsync(
        string filePath, string expectedHex, string displayName, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actualHex = Convert.ToHexString(actual);

        if (!actualHex.Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolVerificationException(
                $"Checksum mismatch for {displayName}. Expected {expectedHex}, got {actualHex}.");
        }

        _logger.LogInformation("Verified {File} against its published SHA-256", displayName);
    }

    private async Task DownloadAsync(
        string url, string targetPath, ExternalTool tool,
        IProgress<ToolInstallProgress>? progress, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to download over a non-HTTPS URL: {url}");

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        var lastReport = 0L;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            // Report roughly every 256 KB rather than every chunk, to keep the UI quiet.
            if (received - lastReport < 256 * 1024) continue;
            lastReport = received;
            progress?.Report(new ToolInstallProgress(tool, ToolInstallPhase.Downloading, received, total));
        }

        progress?.Report(new ToolInstallProgress(tool, ToolInstallPhase.Downloading, received, total));
    }

    /// <summary>
    /// Moves the verified file into place, working around the running executable being locked by
    /// renaming the old copy aside first.
    /// </summary>
    private void ReplaceFile(string source, string target)
    {
        if (File.Exists(target))
        {
            var backup = target + ".old";
            TryDelete(backup);
            try
            {
                File.Move(target, backup);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not move the existing file aside; overwriting in place");
            }
        }

        File.Move(source, target, overwrite: true);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete temporary file {Path}", path);
        }
    }

    /// <summary>Compares two version strings of the form yyyy.MM.dd used by yt-dlp releases.</summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;

        static bool TryParse(string value, out DateOnly date)
            => DateOnly.TryParseExact(value.Trim(), "yyyy.MM.dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);

        if (TryParse(candidate, out var candidateDate) && TryParse(current, out var currentDate))
            return candidateDate > currentDate;

        // Anything that does not look like a dated release falls back to a plain comparison.
        return !string.Equals(candidate.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MiguelDownloader.Engine.Dependencies;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.App.Services;

/// <summary>
/// Builds a diagnostics bundle for troubleshooting.
/// <para>
/// The bundle is meant to be shareable, so it is redacted on the way out: the Windows user name
/// is replaced everywhere it appears in a path, and anything resembling a cookie, token or signed
/// stream URL is removed. Stream URLs matter in particular because they embed the user's IP
/// address and a signature tied to their session.
/// </para>
/// </summary>
public sealed partial class DiagnosticsService(
    SettingsService settings,
    ToolService tools,
    ILogger<DiagnosticsService> logger)
{
    private readonly SettingsService _settings = settings;
    private readonly ToolService _tools = tools;
    private readonly ILogger<DiagnosticsService> _logger = logger;

    [GeneratedRegex(@"https?://[^\s""]*googlevideo\.com/[^\s""]*", RegexOptions.IgnoreCase)]
    private static partial Regex StreamUrlRegex();

    [GeneratedRegex(@"(?i)\b(cookie|set-cookie|authorization|token|password|secret|api[_-]?key)\b\s*[:=]\s*\S+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?i)([?&])(signature|sig|lsig|token|key|ip)=[^&\s]*")]
    private static partial Regex QuerySecretRegex();

    /// <summary>Writes a zip containing the redacted logs and an environment summary.</summary>
    public async Task<bool> ExportAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // Build in a temporary file so a failure never leaves a half-written bundle behind.
            var temporary = Path.Combine(Path.GetTempPath(), $"migueldownloader-diag-{Guid.NewGuid():N}.zip");

            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var summary = archive.CreateEntry("environment.txt");
                await using (var writer = new StreamWriter(summary.Open(), Encoding.UTF8))
                    await writer.WriteAsync(Redact(BuildEnvironmentSummary())).ConfigureAwait(false);

                await AddLogsAsync(archive, cancellationToken).ConfigureAwait(false);
                await AddSettingsAsync(archive, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, targetPath, overwrite: true);
            _logger.LogInformation("Diagnostics exported to {Path}", targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not export diagnostics");
            return false;
        }
    }

    private async Task AddLogsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(App.LogDirectory)) return;

        // Only the recent files: older ones rarely help and make the bundle large.
        var files = new DirectoryInfo(App.LogDirectory)
            .GetFiles("*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(3);

        foreach (var file in files)
        {
            try
            {
                // Shared read: Serilog still holds the current file open.
                await using var source = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source, Encoding.UTF8);

                var entry = archive.CreateEntry($"logs/{file.Name}");
                await using var target = entry.Open();
                await using var writer = new StreamWriter(target, Encoding.UTF8);

                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                    await writer.WriteLineAsync(Redact(line)).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not include the log file {Name}", file.Name);
            }
        }
    }

    private async Task AddSettingsAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var current = _settings.Current;

        // The settings document is rebuilt rather than copied, so the cookie configuration and
        // any custom arguments never leave the machine.
        var text = new StringBuilder()
            .AppendLine("[general]")
            .AppendLine($"language = {current.General.Language}")
            .AppendLine($"theme = {current.General.Theme}")
            .AppendLine($"watchClipboard = {current.General.WatchClipboard}")
            .AppendLine()
            .AppendLine("[video]")
            .AppendLine($"tier = {current.Video.DefaultTier}")
            .AppendLine($"container = {current.Video.DefaultContainer}")
            .AppendLine($"codec = {current.Video.CodecPreference}")
            .AppendLine($"hdrPolicy = {current.Video.Hdr}")
            .AppendLine($"subtitles = {current.Video.DownloadSubtitles}")
            .AppendLine()
            .AppendLine("[music]")
            .AppendLine($"format = {current.Music.Format}")
            .AppendLine($"albumFolders = {current.Music.UseAlbumFolders}")
            .AppendLine($"coverArt = {current.Music.EmbedCoverArt}")
            .AppendLine()
            .AppendLine("[downloads]")
            .AppendLine($"concurrent = {current.Downloads.MaxConcurrentDownloads}")
            .AppendLine($"fragments = {current.Downloads.ConcurrentFragments}")
            .AppendLine($"retries = {current.Downloads.RetryCount}")
            .AppendLine($"speedLimitKbps = {current.Downloads.SpeedLimitKbps?.ToString() ?? "none"}")
            .AppendLine($"existingFilePolicy = {current.Downloads.ExistingFilePolicy}")
            .AppendLine()
            .AppendLine("[advanced]")
            .AppendLine($"autoUpdate = {current.Advanced.AutoUpdateYtDlp}")
            .AppendLine($"verboseLogging = {current.Advanced.VerboseLogging}")
            .AppendLine($"cookiesConfigured = {!string.IsNullOrWhiteSpace(current.Advanced.CookiesFromBrowser) || !string.IsNullOrWhiteSpace(current.Advanced.CookiesFilePath)}")
            .AppendLine($"extraArgumentsPresent = {!string.IsNullOrWhiteSpace(current.Advanced.ExtraYtDlpArguments)}")
            .ToString();

        var entry = archive.CreateEntry("settings.txt");
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(Redact(text)).ConfigureAwait(false);
    }

    private string BuildEnvironmentSummary()
    {
        var toolPaths = _tools.Current;

        return new StringBuilder()
            .AppendLine("Miguel Downloader diagnostics")
            .AppendLine($"generated       : {DateTimeOffset.Now:O}")
            .AppendLine($"app version     : {typeof(DiagnosticsService).Assembly.GetName().Version}")
            .AppendLine($"os              : {Environment.OSVersion}")
            .AppendLine($"64-bit os       : {Environment.Is64BitOperatingSystem}")
            .AppendLine($"runtime         : {Environment.Version}")
            .AppendLine($"processors      : {Environment.ProcessorCount}")
            .AppendLine($"culture         : {System.Globalization.CultureInfo.CurrentUICulture.Name}")
            .AppendLine()
            .AppendLine("Tools")
            .AppendLine($"yt-dlp          : {Describe(toolPaths?.YtDlp)}")
            .AppendLine($"yt-dlp version  : {toolPaths?.YtDlp.Version ?? "unknown"}")
            .AppendLine($"ffmpeg          : {Describe(toolPaths?.Ffmpeg)}")
            .AppendLine($"ffprobe         : {Describe(toolPaths?.Ffprobe)}")
            .AppendLine($"js runtime      : {Describe(toolPaths?.JsRuntime)}")
            .AppendLine()
            .AppendLine("Storage")
            .AppendLine($"data directory  : {App.DataDirectory}")
            .AppendLine($"download folder : {_settings.ResolveDownloadFolder()}")
            .AppendLine($"free space      : {DescribeFreeSpace()}")
            .ToString();

        static string Describe(ResolvedTool? tool)
            => tool is null ? "unresolved" : $"{tool.Source} {tool.Path ?? "(missing)"}";
    }

    private string DescribeFreeSpace()
    {
        var bytes = Engine.Files.DiskSpace.TryGetAvailableBytes(_settings.ResolveDownloadFolder());
        return bytes is null
            ? "unknown"
            : Converters.ByteSizeConverter.Format(bytes.Value);
    }

    /// <summary>
    /// Removes anything that identifies the user or could be replayed.
    /// <para>
    /// Order matters: whole stream URLs go first, because they contain both an IP address and a
    /// signature that the narrower rules would only partly catch.
    /// </para>
    /// </summary>
    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = StreamUrlRegex().Replace(text, "[stream-url-removed]");
        result = SecretRegex().Replace(result, match =>
        {
            var separator = match.Value.Contains(':') ? ':' : '=';
            var key = match.Value.Split(separator)[0];
            return $"{key}{separator} [redacted]";
        });
        result = QuerySecretRegex().Replace(result, "$1$2=[redacted]");

        // The Windows account name appears throughout file paths.
        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length > 2)
            result = result.Replace(userName, "[user]", StringComparison.OrdinalIgnoreCase);

        return result;
    }
}

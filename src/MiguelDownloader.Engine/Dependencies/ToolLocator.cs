using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Processes;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Dependencies;

/// <summary>
/// Finds the external tools.
/// <para>
/// Resolution order is deliberate: an explicit setting wins, then the copy the app manages, then
/// anything shipped beside the executable, then the system PATH. That way a user who points at a
/// specific build always gets it, and everyone else gets the version the app keeps updated
/// rather than whatever happens to be installed globally.
/// </para>
/// </summary>
public sealed class ToolLocator(ProcessRunner runner, ILogger<ToolLocator> logger)
{
    private readonly ProcessRunner _runner = runner;
    private readonly ILogger<ToolLocator> _logger = logger;

    /// <summary>Directory the app downloads and updates its own copies into.</summary>
    public static string ManagedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiguelDownloader", "tools");

    private static string ApplicationDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;

    /// <summary>
    /// Where the installer places the tools it ships with.
    /// <para>
    /// This is what makes the first run ready to use: the tools are already on disk when the
    /// application opens for the first time, so nothing has to be downloaded before the user can
    /// paste a URL. The managed directory still takes precedence, which is how an updated
    /// yt-dlp supersedes the bundled one without needing write access here.
    /// </para>
    /// </summary>
    public static string BundledDirectory => Path.Combine(ApplicationDirectory, "tools");

    /// <summary>Resolves every tool, querying versions where cheap to do so.</summary>
    public async Task<ToolPaths> ResolveAsync(AdvancedSettings advanced, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(advanced);

        var ytDlp = Locate(ExternalTool.YtDlp, advanced.YtDlpPath, ["yt-dlp.exe"]);
        var ffmpeg = Locate(ExternalTool.Ffmpeg, advanced.FfmpegPath, ["ffmpeg.exe"]);
        var ffprobe = Locate(ExternalTool.Ffprobe, advanced.FfprobePath, ["ffprobe.exe"]);
        var jsRuntime = LocateJsRuntime(advanced.JsRuntimePath);

        // Only yt-dlp's version is worth a process launch on startup; the others are reported
        // lazily when the diagnostics page asks for them.
        if (ytDlp.IsAvailable)
        {
            var version = await TryGetVersionAsync(ytDlp.Path!, ["--version"], cancellationToken)
                .ConfigureAwait(false);
            ytDlp = ytDlp with { Version = version };
        }

        return new ToolPaths
        {
            YtDlp = ytDlp,
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            JsRuntime = jsRuntime,
        };
    }

    private ResolvedTool Locate(ExternalTool tool, string? configuredPath, string[] fileNames)
    {
        // 1. An explicit setting. Accept either the executable or its directory.
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var resolved = ResolveConfigured(configuredPath, fileNames);
            if (resolved is not null)
                return new ResolvedTool { Tool = tool, Source = ToolSource.Configured, Path = resolved };

            _logger.LogWarning("Configured path for {Tool} does not exist: {Path}", tool, configuredPath);
        }

        // 2. The managed copy.
        foreach (var name in fileNames)
        {
            var managed = Path.Combine(ManagedDirectory, name);
            if (File.Exists(managed))
                return new ResolvedTool { Tool = tool, Source = ToolSource.Managed, Path = managed };
        }

        // 3. Shipped by the installer, in the application's own tools folder. The folder beside
        //    the executable is also checked, which is what a hand-assembled layout looks like.
        foreach (var directory in (ReadOnlySpan<string>)[BundledDirectory, ApplicationDirectory])
        {
            foreach (var name in fileNames)
            {
                var bundled = Path.Combine(directory, name);
                if (File.Exists(bundled))
                    return new ResolvedTool { Tool = tool, Source = ToolSource.Bundled, Path = bundled };
            }
        }

        // 4. The system PATH.
        foreach (var name in fileNames)
        {
            var onPath = FindOnPath(name);
            if (onPath is not null)
                return new ResolvedTool { Tool = tool, Source = ToolSource.SystemPath, Path = onPath };
        }

        return new ResolvedTool { Tool = tool, Source = ToolSource.Missing };
    }

    private static string? ResolveConfigured(string configuredPath, string[] fileNames)
    {
        if (File.Exists(configuredPath)) return configuredPath;

        if (!Directory.Exists(configuredPath)) return null;

        foreach (var name in fileNames)
        {
            var candidate = Path.Combine(configuredPath, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Finds the JavaScript runtime the extractor uses for YouTube's challenges. Deno is the
    /// runtime yt-dlp enables by default; Node is accepted from version 22 onwards.
    /// </summary>
    private ResolvedTool LocateJsRuntime(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return new ResolvedTool
            {
                Tool = ExternalTool.JsRuntime, Source = ToolSource.Configured, Path = configuredPath,
            };

        var managedDeno = Path.Combine(ManagedDirectory, "deno.exe");
        if (File.Exists(managedDeno))
            return new ResolvedTool
            {
                Tool = ExternalTool.JsRuntime, Source = ToolSource.Managed, Path = managedDeno,
            };

        // The installer ships Deno, so a normal installation always has a runtime and never
        // sees YouTube withhold formats for the lack of one.
        foreach (var directory in (ReadOnlySpan<string>)[BundledDirectory, ApplicationDirectory])
        {
            var bundledDeno = Path.Combine(directory, "deno.exe");
            if (File.Exists(bundledDeno))
                return new ResolvedTool
                {
                    Tool = ExternalTool.JsRuntime, Source = ToolSource.Bundled, Path = bundledDeno,
                };
        }

        // Falling back to whatever the machine already has keeps a source build working.
        foreach (var name in (ReadOnlySpan<string>)["deno.exe", "node.exe"])
        {
            var found = FindOnPath(name);
            if (found is not null)
                return new ResolvedTool
                {
                    Tool = ExternalTool.JsRuntime, Source = ToolSource.SystemPath, Path = found,
                };
        }

        return new ResolvedTool { Tool = ExternalTool.JsRuntime, Source = ToolSource.Missing };
    }

    /// <summary>Searches the PATH for an executable, mirroring what the shell would resolve.</summary>
    public static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try
            {
                // A malformed PATH entry must not take the whole lookup down.
                candidate = Path.Combine(directory.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Runs a tool with a version flag, returning its first output line.</summary>
    public async Task<string?> TryGetVersionAsync(
        string executablePath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // A hung tool must not stall application startup.
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var result = await _runner.RunAsync(
                executablePath, arguments,
                captureStandardOutput: true,
                cancellationToken: timeout.Token).ConfigureAwait(false);

            if (!result.Succeeded) return null;

            return result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out querying version of {Path}", executablePath);
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not query version of {Path}", executablePath);
            return null;
        }
    }
}

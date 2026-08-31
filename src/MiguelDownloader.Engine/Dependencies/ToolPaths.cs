using MiguelDownloader.Core.Settings;

namespace MiguelDownloader.Engine.Dependencies;

/// <summary>Which external tool a status or error refers to.</summary>
public enum ExternalTool
{
    YtDlp = 0,
    Ffmpeg,
    Ffprobe,
    JsRuntime,
}

/// <summary>Where a resolved tool came from, so the UI can explain what it is using.</summary>
public enum ToolSource
{
    /// <summary>Not found anywhere.</summary>
    Missing = 0,
    /// <summary>An explicit path from the advanced settings.</summary>
    Configured,
    /// <summary>The copy the app downloaded and manages itself.</summary>
    Managed,
    /// <summary>Found on the system PATH.</summary>
    SystemPath,
    /// <summary>Shipped next to the application executable.</summary>
    Bundled,
}

/// <summary>One resolved tool.</summary>
public sealed record ResolvedTool
{
    public required ExternalTool Tool { get; init; }
    public required ToolSource Source { get; init; }

    /// <summary>Absolute path, or null when the tool was not found.</summary>
    public string? Path { get; init; }

    /// <summary>Version string as reported by the tool, when it could be queried.</summary>
    public string? Version { get; init; }

    public bool IsAvailable => Path is { Length: > 0 } && Source != ToolSource.Missing;
}

/// <summary>
/// The tools the engine needs, resolved for this run.
/// <para>
/// yt-dlp and ffmpeg are required for anything beyond metadata; ffprobe is required to validate
/// finished files; the JavaScript runtime is optional but improves which formats YouTube
/// publishes, so its absence is reported as a warning rather than an error.
/// </para>
/// </summary>
public sealed record ToolPaths
{
    public required ResolvedTool YtDlp { get; init; }
    public required ResolvedTool Ffmpeg { get; init; }
    public required ResolvedTool Ffprobe { get; init; }
    public required ResolvedTool JsRuntime { get; init; }

    /// <summary>
    /// Use encoders available in the Store package's LGPL FFmpeg build when a user explicitly
    /// requests a container that forces video conversion. Normal downloads keep copying streams.
    /// </summary>
    public bool UseLgplTranscodeEncoders { get; init; }

    /// <summary>
    /// Whether paths and extra yt-dlp arguments supplied in user settings may influence process
    /// execution. Packaged builds turn this off so every executable remains inside the signed
    /// package and receives updates only from the package source.
    /// </summary>
    public bool AllowUserToolOverrides { get; init; } = true;

    /// <summary>
    /// Applies the distribution's execution policy while preserving non-executable preferences
    /// such as cookies and logging. In package mode, resolved bundled paths replace user paths
    /// and arbitrary extra yt-dlp arguments are ignored.
    /// </summary>
    public AdvancedSettings ApplyExecutionPolicy(AdvancedSettings advanced)
    {
        ArgumentNullException.ThrowIfNull(advanced);
        if (AllowUserToolOverrides) return advanced;

        return advanced with
        {
            YtDlpPath = string.Empty,
            FfmpegPath = Ffmpeg.Path ?? string.Empty,
            FfprobePath = Ffprobe.Path ?? string.Empty,
            JsRuntimePath = JsRuntime.Path ?? string.Empty,
            ExtraYtDlpArguments = string.Empty,
        };
    }

    /// <summary>True when downloads can run at all.</summary>
    public bool CanDownload => YtDlp.IsAvailable;

    /// <summary>True when merging, remuxing and conversion are possible.</summary>
    public bool CanProcess => Ffmpeg.IsAvailable;

    /// <summary>True when finished files can be verified.</summary>
    public bool CanValidate => Ffprobe.IsAvailable;

    /// <summary>Tools that are required but missing.</summary>
    public IReadOnlyList<ExternalTool> MissingRequired
    {
        get
        {
            var missing = new List<ExternalTool>();
            if (!YtDlp.IsAvailable) missing.Add(ExternalTool.YtDlp);
            if (!Ffmpeg.IsAvailable) missing.Add(ExternalTool.Ffmpeg);
            if (!Ffprobe.IsAvailable) missing.Add(ExternalTool.Ffprobe);
            return missing;
        }
    }

    public bool IsComplete => MissingRequired.Count == 0;
}

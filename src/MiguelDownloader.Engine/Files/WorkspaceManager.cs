using MiguelDownloader.Core.Naming;
using MiguelDownloader.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Files;

/// <summary>How a name collision was resolved.</summary>
public enum CollisionOutcome
{
    /// <summary>No file was in the way.</summary>
    NoCollision = 0,
    Renamed,
    Overwrite,
    Skip,
    /// <summary>The caller must ask the user before anything else can happen.</summary>
    NeedsUserDecision,
}

/// <summary>The result of preparing a destination.</summary>
/// <param name="Outcome">What was decided.</param>
/// <param name="FileName">The name to actually write, after any rename.</param>
public readonly record struct DestinationPlan(CollisionOutcome Outcome, string FileName);

/// <summary>
/// Owns the working directories and the move into the final destination.
/// <para>
/// Each job downloads into its own empty folder. That keeps partial files, fragment files and
/// sidecars from different jobs apart, makes cleanup a single directory delete, and means the
/// finished file can be identified by looking at what appeared rather than by predicting a name
/// the downloader might adjust.
/// </para>
/// </summary>
public sealed class WorkspaceManager(ILogger<WorkspaceManager> logger)
{
    private readonly ILogger<WorkspaceManager> _logger = logger;
    private const string MarkerFileName = ".migueldownloader-workspace";

    /// <summary>Root for all working directories.</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiguelDownloader", "work");

    /// <summary>Files the downloader leaves behind that are never the result.</summary>
    private static readonly string[] NonMediaExtensions =
    [
        ".part", ".ytdl", ".temp", ".tmp", ".json", ".description", ".annotations.xml",
    ];

    private static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa", ".lrc", ".ttml", ".srv3"];
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Creates a private working directory for one job.</summary>
    public string CreateWorkspace(string taskId, DownloadSettings settings)
    {
        if (string.IsNullOrWhiteSpace(taskId) || !Path.GetFileName(taskId).Equals(taskId, StringComparison.Ordinal))
            throw new ArgumentException("A workspace task id must be a single path segment.", nameof(taskId));

        var root = ResolveRoot(settings);

        var path = Path.Combine(root, taskId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, MarkerFileName), taskId);
        return path;
    }

    /// <summary>
    /// Removes a working directory. Called after a successful move, and after a failure whose
    /// partial data cannot be resumed.
    /// </summary>
    public void CleanWorkspace(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return;

        if (!IsOwnedWorkspace(workspace))
        {
            _logger.LogWarning("Refused to remove an unmarked working directory: {Path}", workspace);
            return;
        }

        try
        {
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover folder is untidy but harmless, and sweeping runs at next startup.
            _logger.LogWarning(ex, "Could not remove the working directory {Path}", workspace);
        }
    }

    /// <summary>
    /// Deletes working directories that no live job owns. Runs at startup to clear folders left
    /// by a crash or a forced close.
    /// </summary>
    public int SweepOrphans(IReadOnlySet<string> liveTaskIds, DownloadSettings settings)
    {
        var root = ResolveRoot(settings);
        if (!Directory.Exists(root)) return 0;

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (liveTaskIds.Contains(name)) continue;
            if (!IsOwnedWorkspace(directory))
            {
                _logger.LogDebug("Ignoring an unmarked directory inside the temporary root: {Path}", directory);
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Orphan working directory still in use: {Path}", directory);
            }
        }

        if (removed > 0) _logger.LogInformation("Removed {Count} orphaned working directories", removed);
        return removed;
    }

    private static bool IsOwnedWorkspace(string directory)
        => File.Exists(Path.Combine(directory, MarkerFileName));

    /// <summary>
    /// Keeps application-owned workspaces inside a dedicated child when the user chooses a custom
    /// temporary location. The selected folder may already contain unrelated directories; treating
    /// it as an exclusive root would make orphan cleanup delete data the application does not own.
    /// </summary>
    public static string ResolveRoot(DownloadSettings settings)
        => string.IsNullOrWhiteSpace(settings.TemporaryFolder)
            ? DefaultRoot
            : Path.Combine(settings.TemporaryFolder, "MiguelDownloader-work");

    /// <summary>
    /// Finds the media file a job produced. The workspace started empty, so the largest file
    /// that is not a known sidecar is the result.
    /// </summary>
    public string? FindProducedMedia(string workspace)
    {
        if (!Directory.Exists(workspace)) return null;

        return Directory.EnumerateFiles(workspace, "*", SearchOption.TopDirectoryOnly)
            .Where(IsCandidateMedia)
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        static bool IsCandidateMedia(string path)
        {
            if (Path.GetFileName(path).Equals(MarkerFileName, StringComparison.OrdinalIgnoreCase))
                return false;

            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (NonMediaExtensions.Contains(extension)) return false;
            if (SubtitleExtensions.Contains(extension)) return false;
            if (ImageExtensions.Contains(extension)) return false;
            return new FileInfo(path).Length > 0;
        }
    }

    /// <summary>Lists the subtitle files a job produced, so they can be moved alongside the media.</summary>
    public IReadOnlyList<string> FindSubtitleFiles(string workspace)
        => Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace)
                .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList()
            : [];

    /// <summary>Lists the image files a job produced, for cover art written as a separate file.</summary>
    public IReadOnlyList<string> FindImageFiles(string workspace)
        => Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList()
            : [];

    /// <summary>
    /// Decides the final file name, applying the configured collision policy.
    /// </summary>
    public DestinationPlan PlanDestination(
        string directory, string fileName, ExistingFilePolicy policy)
    {
        Directory.CreateDirectory(directory);

        var fitted = FileNameSanitizer.EnsurePathFits(directory, fileName);
        var fullPath = Path.Combine(directory, fitted);

        if (!File.Exists(fullPath)) return new DestinationPlan(CollisionOutcome.NoCollision, fitted);

        return policy switch
        {
            ExistingFilePolicy.Skip => new DestinationPlan(CollisionOutcome.Skip, fitted),
            ExistingFilePolicy.Overwrite => new DestinationPlan(CollisionOutcome.Overwrite, fitted),
            ExistingFilePolicy.RenameAutomatically => new DestinationPlan(
                CollisionOutcome.Renamed,
                FileNameSanitizer.MakeUnique(directory, fitted, File.Exists)),
            _ => new DestinationPlan(CollisionOutcome.NeedsUserDecision, fitted),
        };
    }

    /// <summary>
    /// Moves a finished file to its destination.
    /// <para>
    /// A plain move is tried first, which is atomic when both paths are on the same volume. It
    /// fails across volumes, so the fallback copies and only removes the source once the copy has
    /// completed, meaning an interruption can never destroy the downloaded data.
    /// </para>
    /// </summary>
    public async Task<string> MoveIntoPlaceAsync(
        string sourcePath, string targetDirectory, string targetFileName, bool overwrite,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, targetFileName);

        if (overwrite && File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        try
        {
            File.Move(sourcePath, targetPath, overwrite);
            return targetPath;
        }
        catch (IOException) when (!AreSameVolume(sourcePath, targetPath))
        {
            _logger.LogDebug("Cross-volume move; copying instead");
        }

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         81920, useAsync: true))
        await using (var destination = new FileStream(targetPath, overwrite ? FileMode.Create : FileMode.CreateNew,
                         FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            File.Delete(sourcePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Copied the file but could not remove the source");
        }

        return targetPath;
    }

    private static bool AreSameVolume(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(a)),
                Path.GetPathRoot(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

namespace MiguelDownloader.Engine.Files;

/// <summary>The outcome of a free-space check.</summary>
/// <param name="IsSufficient">False when the download should not start.</param>
/// <param name="AvailableBytes">Free space on the target volume, or null when it could not be read.</param>
/// <param name="RequiredBytes">Estimated requirement, including working space.</param>
public readonly record struct DiskSpaceCheck(bool IsSufficient, long? AvailableBytes, long RequiredBytes)
{
    /// <summary>True when the check could not be performed and the caller should simply proceed.</summary>
    public bool IsUnknown => AvailableBytes is null;
}

/// <summary>
/// Estimates whether a download will fit.
/// <para>
/// The final file is not the whole story: separate video and audio streams both land on disk
/// before being combined, and remuxing writes a second copy before the first is removed. The
/// estimate accounts for that, because running out of space halfway through a large download
/// wastes far more of the user's time than a warning up front costs.
/// </para>
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// Working space multiplier. Downloading video and audio separately and then writing the
    /// combined output means roughly twice the final size is touched before cleanup.
    /// </summary>
    private const double WorkingSpaceFactor = 2.2;

    /// <summary>A margin so the volume is never driven to literally zero free bytes.</summary>
    private const long SafetyMarginBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Checks whether <paramref name="estimatedFinalSize"/> can be downloaded to
    /// <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="targetPath">A file or directory on the destination volume.</param>
    /// <param name="estimatedFinalSize">
    /// Expected size of the finished file. Null when unknown, in which case the check passes:
    /// blocking a download over a number we do not have would be worse than letting it run.
    /// </param>
    public static DiskSpaceCheck Check(string targetPath, long? estimatedFinalSize)
    {
        if (estimatedFinalSize is null or <= 0)
            return new DiskSpaceCheck(true, TryGetAvailableBytes(targetPath), 0);

        var required = (long)(estimatedFinalSize.Value * WorkingSpaceFactor) + SafetyMarginBytes;
        var available = TryGetAvailableBytes(targetPath);

        // An unreadable volume (a network share, a mapped drive that is momentarily away) is not
        // grounds to refuse; the download will report a real error if it genuinely cannot write.
        if (available is null) return new DiskSpaceCheck(true, null, required);

        return new DiskSpaceCheck(available.Value >= required, available, required);
    }

    /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null if unknown.</summary>
    public static long? TryGetAvailableBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}

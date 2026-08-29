using System.Globalization;
using System.Text;

namespace MiguelDownloader.Core.Naming;

/// <summary>
/// Makes arbitrary titles safe to use as Windows file and directory names.
/// <para>
/// Windows rejects more than just the obvious characters: device names like <c>CON</c> and
/// <c>LPT1</c> are reserved even with an extension, trailing dots and spaces are silently
/// stripped by the shell, and the whole path has a length ceiling. Video titles routinely contain
/// colons, slashes and quotes, so this runs over every name the app writes.
/// </para>
/// </summary>
public static class FileNameSanitizer
{
    /// <summary>
    /// Device names reserved by Windows. Reserved with or without an extension, case-insensitively.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Visually similar stand-ins, so a sanitised title still reads naturally instead of turning
    /// into a row of underscores.
    /// </summary>
    private static readonly Dictionary<char, string> Replacements = new()
    {
        ['<'] = "(",
        ['>'] = ")",
        [':'] = " -",
        ['"'] = "'",
        ['/'] = "-",
        ['\\'] = "-",
        ['|'] = "-",
        ['?'] = "",
        ['*'] = "",
    };

    /// <summary>Conservative ceiling for a single path component.</summary>
    public const int MaxComponentLength = 150;

    /// <summary>
    /// Cleans one path component. Never returns an empty string: unusable input yields
    /// <paramref name="fallback"/>.
    /// </summary>
    public static string SanitizeComponent(string? name, string fallback = "sem-titulo")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            // Control characters have no printable form and break several tools outright.
            if (char.IsControl(ch)) { sb.Append(' '); continue; }

            if (Replacements.TryGetValue(ch, out var replacement)) { sb.Append(replacement); continue; }

            sb.Append(ch);
        }

        var result = CollapseWhitespace(sb.ToString());

        // The shell strips trailing dots and spaces, so a name ending in one cannot round-trip.
        result = result.TrimEnd('.', ' ');
        result = result.TrimStart(' ');

        if (result.Length == 0) return fallback;

        result = Truncate(result, MaxComponentLength);

        // Re-check after truncation, which can expose a new trailing dot.
        result = result.TrimEnd('.', ' ');
        if (result.Length == 0) return fallback;

        if (IsReserved(result)) result = "_" + result;

        return result;
    }

    /// <summary>True when the name collides with a Windows device name, extension aside.</summary>
    public static bool IsReserved(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var stem = name;
        var dot = stem.IndexOf('.');
        if (dot > 0) stem = stem[..dot];
        return ReservedNames.Contains(stem.TrimEnd(' ', '.'));
    }

    /// <summary>
    /// Shortens to <paramref name="max"/> characters without splitting a surrogate pair, which
    /// would otherwise leave a broken character at the end of the name.
    /// </summary>
    public static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;

        var cut = max;
        if (char.IsHighSurrogate(value[cut - 1])) cut--;

        // Prefer to break on a word boundary when one is close enough to keep the name readable.
        var lastSpace = value.LastIndexOf(' ', Math.Max(0, cut - 1));
        if (lastSpace > max * 0.6) cut = lastSpace;

        return value[..cut].TrimEnd();
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var ch in value)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!lastWasSpace) sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
            lastWasSpace = isSpace;
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Keeps a full path within the Windows limit by shortening the file name, never the
    /// directories, since the directory layout is what the user configured.
    /// </summary>
    /// <param name="directory">Target directory, already sanitised.</param>
    /// <param name="fileName">File name including extension.</param>
    /// <param name="maxPathLength">
    /// Ceiling for the whole path. The classic limit is 260; the default leaves room for the
    /// <c>.part</c> and <c>.temp</c> suffixes the download pipeline appends while working.
    /// </param>
    /// <param name="reserve">
    /// Extra characters to hold back beyond the directory itself.
    /// <para>
    /// A download does not live only where it lands. It is written into a working directory that
    /// is usually longer than the destination, and yt-dlp appends <c>.f&lt;format-id&gt;</c> to each
    /// part before merging them. Budgeting against the destination alone produces a name that fits
    /// where the file ends up and not where it is built, which fails on a path the user never sees.
    /// </para>
    /// </param>
    public static string EnsurePathFits(
        string directory, string fileName, int maxPathLength = 240, int reserve = 0)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);

        var separatorAllowance = directory.EndsWith(Path.DirectorySeparatorChar) ? 0 : 1;
        var available = maxPathLength - directory.Length - separatorAllowance - reserve;

        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);

        // Nothing sensible left: keep a minimal but unique-ish stem rather than an empty name.
        if (available <= extension.Length + 8)
            return "arquivo" + extension;

        if (fileName.Length <= available) return fileName;

        var stemBudget = available - extension.Length;
        return Truncate(stem, stemBudget).TrimEnd('.', ' ') + extension;
    }

    /// <summary>
    /// Finds a name that does not collide with an existing file by appending " (2)", " (3)"...
    /// </summary>
    /// <param name="exists">
    /// Existence probe. Injected so the rule can be tested without touching the file system.
    /// </param>
    public static string MakeUnique(string directory, string fileName, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var candidate = Path.Combine(directory, fileName);
        if (!exists(candidate)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 10_000; i++)
        {
            var suffix = string.Create(CultureInfo.InvariantCulture, $" ({i})");
            var trimmedStem = FileNameSanitizer.Truncate(stem, Math.Max(1, MaxComponentLength - suffix.Length));
            var next = trimmedStem + suffix + extension;
            if (!exists(Path.Combine(directory, next))) return next;
        }

        // Astronomically unlikely; a timestamp guarantees termination.
        return $"{stem} ({DateTime.Now:yyyyMMddHHmmss}){extension}";
    }
}

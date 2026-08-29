using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MiguelDownloader.Core.Naming;

/// <summary>
/// Expands file-name templates written in the familiar <c>%(field)s</c> style.
/// <para>
/// The syntax mirrors yt-dlp so it reads as expected, but expansion happens here rather than
/// being delegated to the downloader. The app needs the final path up front to check for
/// collisions, enforce the Windows path limit and lay out album directories, none of which is
/// possible if the name is only decided inside another process.
/// </para>
/// <para>
/// Supported forms: <c>%(field)s</c> for text, <c>%(field)d</c> for a number, and
/// <c>%(field)02d</c> for a zero-padded number. Unknown fields expand to an empty string, and
/// any separator left stranded by an empty field is cleaned up afterwards.
/// </para>
/// </summary>
public static partial class NameTemplate
{
    [GeneratedRegex(@"%\((?<field>[a-zA-Z_][a-zA-Z0-9_]*)\)(?<pad>0\d+)?(?<type>[sd])",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    /// <summary>Templates offered in the UI, kept here so settings and tests agree on the defaults.</summary>
    public static class Defaults
    {
        public const string Video = "%(title)s [%(id)s]";
        public const string VideoSimple = "%(title)s";
        public const string PlaylistItem = "%(playlist_index)02d - %(title)s";
        public const string MusicTrack = "%(track_number)02d - %(title)s";
        public const string MusicSingle = "%(artist)s - %(title)s";
        public const string AlbumDirectory = "%(album_artist)s/%(album)s";
        public const string AlbumDirectoryWithYear = "%(album_artist)s/%(album)s (%(year)d)";
        public const string PlaylistDirectory = "%(playlist_title)s";
    }

    /// <summary>
    /// Expands <paramref name="template"/> against <paramref name="values"/>.
    /// The result is sanitised per path component, so a template may contain <c>/</c> to build
    /// a directory layout without letting a field value inject one.
    /// </summary>
    /// <param name="template">Template text.</param>
    /// <param name="values">Field values. Null or empty values make their token vanish.</param>
    /// <param name="allowSubdirectories">
    /// When true, <c>/</c> in the template separates directories. Field values never produce a
    /// separator regardless, because they are sanitised individually.
    /// </param>
    public static string Expand(
        string template,
        IReadOnlyDictionary<string, object?> values,
        bool allowSubdirectories = false)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var expanded = TokenRegex().Replace(template, match =>
        {
            var field = match.Groups["field"].Value;
            if (!values.TryGetValue(field, out var value) || value is null) return string.Empty;

            var type = match.Groups["type"].Value;
            var pad = match.Groups["pad"].Value;

            if (type == "d")
            {
                if (!TryToLong(value, out var number)) return string.Empty;
                if (pad.Length > 1 && int.TryParse(pad[1..], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var width))
                    return number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                return number.ToString(CultureInfo.InvariantCulture);
            }

            var text = value as string ?? Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            // Sanitise the value, not the template, so a title containing "/" cannot create a directory.
            return text.Length == 0 ? string.Empty : SanitizeFieldValue(text);
        });

        return Tidy(expanded, allowSubdirectories);
    }

    /// <summary>
    /// Removes separators left dangling by empty fields, so a missing artist does not yield
    /// " - Title" and a missing track number does not yield "00 - ".
    /// </summary>
    private static string Tidy(string value, bool allowSubdirectories)
    {
        var segments = allowSubdirectories
            ? value.Split('/', StringSplitOptions.None)
            : [value.Replace('/', '-')];

        var cleaned = new List<string>(segments.Length);
        foreach (var raw in segments)
        {
            var s = raw;

            s = MultiSpaceRegex().Replace(s, " ");
            s = s.Trim();

            // Strip separators that now sit at an edge because their neighbour expanded to nothing.
            s = TrimSeparators(s);
            s = MultiSpaceRegex().Replace(s, " ").Trim();

            // Collapse " -  - " runs from two consecutive empty fields.
            while (s.Contains(" -  - ", StringComparison.Ordinal))
                s = s.Replace(" -  - ", " - ", StringComparison.Ordinal);
            while (s.Contains("- -", StringComparison.Ordinal))
                s = s.Replace("- -", "-", StringComparison.Ordinal);

            // An empty pair of brackets is the usual leftover of a missing id.
            s = s.Replace("[]", string.Empty, StringComparison.Ordinal)
                 .Replace("()", string.Empty, StringComparison.Ordinal);

            s = MultiSpaceRegex().Replace(s, " ").Trim();
            s = TrimSeparators(s);

            if (s.Length > 0) cleaned.Add(s);
        }

        if (cleaned.Count == 0) return string.Empty;
        return string.Join("/", cleaned);
    }

    private static string TrimSeparators(string s)
    {
        var start = 0;
        var end = s.Length;

        while (start < end && (s[start] is ' ' or '-' or '_' or '.')) start++;
        while (end > start && (s[end - 1] is ' ' or '-' or '_')) end--;

        return s[start..end];
    }

    /// <summary>
    /// Cleans a single expanded value. Path separators become dashes so a field can never escape
    /// its component, and the rest defers to the shared Windows rules.
    /// </summary>
    private static string SanitizeFieldValue(string value)
    {
        var sanitized = FileNameSanitizer.SanitizeComponent(value, fallback: string.Empty);
        return sanitized;
    }

    private static bool TryToLong(object value, out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case double d: result = (long)Math.Round(d); return true;
            case decimal m: result = (long)Math.Round(m); return true;
            case string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p):
                result = p; return true;
            default: result = 0; return false;
        }
    }

    /// <summary>
    /// Checks a template for tokens the app does not know, so settings can reject a typo instead
    /// of silently producing files with pieces missing.
    /// </summary>
    public static IReadOnlyList<string> FindUnknownFields(string template, IEnumerable<string> knownFields)
    {
        ArgumentNullException.ThrowIfNull(template);
        var known = new HashSet<string>(knownFields, StringComparer.OrdinalIgnoreCase);

        return TokenRegex().Matches(template)
            .Select(m => m.Groups["field"].Value)
            .Where(f => !known.Contains(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Every field the app can supply. Also drives the token help in settings.</summary>
    public static IReadOnlyList<string> KnownFields { get; } =
    [
        "title", "id", "uploader", "channel", "artist", "album", "album_artist",
        "track_number", "track_total", "disc_number", "year", "upload_date",
        "playlist_title", "playlist_index", "playlist_id", "resolution", "height",
        "fps", "vcodec", "acodec", "ext", "duration", "genre",
    ];

    /// <summary>Builds the value bag from the pieces the app has, leaving unknowns out entirely.</summary>
    public static Dictionary<string, object?> BuildValues(params (string Key, object? Value)[] pairs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs)
        {
            if (value is string s && string.IsNullOrWhiteSpace(s)) continue;
            if (value is null) continue;
            dict[key] = value;
        }
        return dict;
    }
}

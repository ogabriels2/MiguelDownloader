using System.Globalization;
using System.Text.Json;

namespace MiguelDownloader.Engine.YtDlp;

/// <summary>
/// Defensive readers over the extractor's JSON.
/// <para>
/// yt-dlp tracks a moving target, and its output shape changes between releases: fields appear,
/// disappear, and occasionally change type (a count arriving as a string, a duration as an int
/// where a float was expected). Every accessor here returns null instead of throwing, so a
/// surprise in one field degrades that field alone rather than failing the whole analysis.
/// </para>
/// </summary>
internal static class JsonReader
{
    public static JsonElement? Prop(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind is not JsonValueKind.Null
            ? value
            : null;

    public static string? String(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        if (prop is null) return null;

        return prop.Value.ValueKind switch
        {
            JsonValueKind.String => Blank(prop.Value.GetString()),
            JsonValueKind.Number => prop.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static double? Double(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        if (prop is null) return null;

        return prop.Value.ValueKind switch
        {
            JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d,
            // yt-dlp writes "NA" for unknown values under --progress-template, and occasionally
            // emits numbers as strings.
            JsonValueKind.String when double.TryParse(prop.Value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static long? Long(this JsonElement element, string name)
    {
        var d = element.Double(name);
        if (d is null) return null;
        if (double.IsNaN(d.Value) || double.IsInfinity(d.Value)) return null;
        if (d.Value is > 9.2e18 or < -9.2e18) return null;
        return (long)Math.Round(d.Value);
    }

    public static int? Int(this JsonElement element, string name)
    {
        var l = element.Long(name);
        return l is null or > int.MaxValue or < int.MinValue ? null : (int)l.Value;
    }

    public static bool Bool(this JsonElement element, string name, bool fallback = false)
    {
        var prop = element.Prop(name);
        if (prop is null) return fallback;

        return prop.Value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when prop.Value.TryGetDouble(out var d) => d != 0,
            JsonValueKind.String => bool.TryParse(prop.Value.GetString(), out var b) ? b : fallback,
            _ => fallback,
        };
    }

    /// <summary>Enumerates an array property, yielding nothing when it is absent or not an array.</summary>
    public static IEnumerable<JsonElement> Array(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        if (prop is null || prop.Value.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in prop.Value.EnumerateArray()) yield return item;
    }

    /// <summary>Enumerates an object property's members, yielding nothing when it is not an object.</summary>
    public static IEnumerable<JsonProperty> Members(this JsonElement element, string name)
    {
        var prop = element.Prop(name);
        if (prop is null || prop.Value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var member in prop.Value.EnumerateObject()) yield return member;
    }

    /// <summary>Parses the YYYYMMDD form yt-dlp uses for upload dates.</summary>
    public static DateOnly? CompactDate(this JsonElement element, string name)
    {
        var raw = element.String(name);
        if (raw is null || raw.Length != 8) return null;
        return DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>Reads a duration in seconds, tolerating the fractional values the extractor emits.</summary>
    public static TimeSpan? Duration(this JsonElement element, string name)
    {
        var seconds = element.Double(name);
        if (seconds is null or <= 0) return null;
        if (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value)) return null;
        // A day is far beyond any legitimate upload and signals a bad value.
        return seconds.Value > 86_400 * 7 ? null : TimeSpan.FromSeconds(seconds.Value);
    }
}

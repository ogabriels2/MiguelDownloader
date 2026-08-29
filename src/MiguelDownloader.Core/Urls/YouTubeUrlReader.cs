using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MiguelDownloader.Core.Urls;

/// <summary>
/// Classifies YouTube / YouTube Music URLs from their text alone.
/// <para>
/// This never touches the network: it decides <em>what kind of thing</em> a URL is so the app can
/// pick the right analysis strategy and warn about bulk expansions before spending a request.
/// The authoritative metadata always comes from the extractor afterwards.
/// </para>
/// </summary>
public static partial class YouTubeUrlReader
{
    // A YouTube video id is exactly 11 chars of the URL-safe base64 alphabet.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)]
    private static partial Regex VideoIdRegex();

    // Playlist ids are longer and use the same alphabet.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex HandleRegex();

    // Finds the first http(s) URL inside arbitrary text (clipboard, drag and drop payloads).
    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlInTextRegex();

    [GeneratedRegex(@"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ClockTimeRegex();

    private static readonly string[] YouTubeHosts =
    [
        "youtube.com", "www.youtube.com", "m.youtube.com",
        "music.youtube.com", "gaming.youtube.com", "studio.youtube.com",
        "youtube-nocookie.com", "www.youtube-nocookie.com",
    ];

    private static readonly string[] ShortHosts = ["youtu.be", "www.youtu.be"];

    /// <summary>Pulls the first URL out of free text, e.g. a clipboard payload with surrounding words.</summary>
    public static string? ExtractFirstUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();

        var m = UrlInTextRegex().Match(trimmed);
        if (m.Success)
        {
            // Trailing punctuation and closing brackets are almost never part of a pasted URL.
            return m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        }

        // Bare "youtu.be/x" with no scheme is common when copied out of a chat message.
        foreach (var host in ShortHosts.Concat(YouTubeHosts))
        {
            var idx = trimmed.IndexOf(host, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (idx == 0 || !char.IsLetterOrDigit(trimmed[idx - 1])))
                return "https://" + trimmed[idx..].Split(' ', '\n', '\r', '\t')[0];
        }
        return null;
    }

    /// <summary>True when the text contains something we would recognise as a downloadable YouTube URL.</summary>
    public static bool LooksLikeYouTubeUrl(string? text)
        => TryParse(ExtractFirstUrl(text) ?? text ?? string.Empty, out var info) && info.IsDownloadable;

    public static bool TryParse(string? input, [NotNullWhen(true)] out MediaUrlInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var original = input.Trim();
        var raw = original.Trim('"', '\'', '<', '>');
        if (raw.Length == 0) return false;

        // Tolerate a missing scheme: "youtu.be/x" becomes "https://youtu.be/x".
        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw.TrimStart('/');

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var host = uri.Host.ToLowerInvariant();
        var isMusic = host.Equals("music.youtube.com", StringComparison.Ordinal);
        var query = ParseQuery(uri.Query);
        var startAt = ParseStartTime(query, uri.Fragment);

        if (ShortHosts.Contains(host))
        {
            var shortId = uri.AbsolutePath.Trim('/').Split('/')[0];
            return TryBuildVideo(shortId, MediaUrlKind.Video, original, isMusic: false,
                GetPlaylist(query), GetIndex(query), startAt, out info);
        }

        if (!YouTubeHosts.Contains(host)) return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var first = segments.Length > 0 ? segments[0] : string.Empty;

        switch (first.ToLowerInvariant())
        {
            case "watch":
            {
                var id = query.GetValueOrDefault("v");
                var list = GetPlaylist(query);
                if (string.IsNullOrEmpty(id))
                    return list is not null && TryBuildPlaylist(list, original, isMusic, out info);
                return TryBuildVideo(id, MediaUrlKind.Video, original, isMusic, list, GetIndex(query), startAt, out info);
            }

            case "shorts" when segments.Length > 1:
                return TryBuildVideo(segments[1], MediaUrlKind.Short, original, isMusic,
                    GetPlaylist(query), null, startAt, out info);

            // /embed/videoseries?list=... is a playlist; /embed/<id> is a video.
            case "embed" when segments.Length > 1 && segments[1].Equals("videoseries", StringComparison.OrdinalIgnoreCase):
                return TryBuildPlaylist(GetPlaylist(query), original, isMusic, out info);

            case "embed" when segments.Length > 1:
            case "live" when segments.Length > 1:
            case "v" when segments.Length > 1:
                return TryBuildVideo(segments[1], MediaUrlKind.Video, original, isMusic,
                    GetPlaylist(query), null, startAt, out info);

            case "playlist":
                return TryBuildPlaylist(GetPlaylist(query), original, isMusic, out info);

            case "channel" when segments.Length > 1:
                info = new MediaUrlInfo
                {
                    Kind = MediaUrlKind.Channel,
                    OriginalUrl = original,
                    CanonicalUrl = BuildChannelUrl($"channel/{segments[1]}", segments, out var tabC),
                    ChannelId = segments[1],
                    Tab = tabC,
                    IsMusicDomain = isMusic,
                };
                return true;

            case "c" when segments.Length > 1:
            case "user" when segments.Length > 1:
                info = new MediaUrlInfo
                {
                    Kind = MediaUrlKind.Channel,
                    OriginalUrl = original,
                    CanonicalUrl = BuildChannelUrl($"{first.ToLowerInvariant()}/{segments[1]}", segments, out var tabL),
                    LegacyChannelName = segments[1],
                    Tab = tabL,
                    IsMusicDomain = isMusic,
                };
                return true;

            case "results":
                info = new MediaUrlInfo
                {
                    Kind = MediaUrlKind.Search,
                    OriginalUrl = original,
                    CanonicalUrl = raw,
                    SearchQuery = query.GetValueOrDefault("search_query"),
                    IsMusicDomain = isMusic,
                };
                return true;

            // music.youtube.com/browse/MPREb_... is an album page; other browse ids are not downloadable.
            case "browse" when segments.Length > 1 && segments[1].StartsWith("MPREb", StringComparison.Ordinal):
                info = new MediaUrlInfo
                {
                    Kind = MediaUrlKind.Album,
                    OriginalUrl = original,
                    CanonicalUrl = $"https://music.youtube.com/browse/{segments[1]}",
                    PlaylistId = segments[1],
                    IsMusicDomain = true,
                };
                return true;

            default:
                if (first.StartsWith('@') && first.Length > 1 && HandleRegex().IsMatch(first[1..]))
                {
                    info = new MediaUrlInfo
                    {
                        Kind = MediaUrlKind.Channel,
                        OriginalUrl = original,
                        CanonicalUrl = BuildChannelUrl(first, segments, out var tabH),
                        Handle = first[1..],
                        Tab = tabH,
                        IsMusicDomain = isMusic,
                    };
                    return true;
                }
                return false;
        }
    }

    private static bool TryBuildVideo(
        string? id, MediaUrlKind kind, string original, bool isMusic,
        string? playlistId, int? index, TimeSpan? startAt,
        [NotNullWhen(true)] out MediaUrlInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(id) || !VideoIdRegex().IsMatch(id)) return false;

        // Canonicalise to the watch form. The &list= context is kept as data but deliberately left
        // out of the URL, so analysing one video never silently expands into its whole playlist.
        var canonical = isMusic
            ? $"https://music.youtube.com/watch?v={id}"
            : $"https://www.youtube.com/watch?v={id}";

        info = new MediaUrlInfo
        {
            Kind = kind,
            OriginalUrl = original,
            CanonicalUrl = canonical,
            VideoId = id,
            PlaylistId = playlistId,
            PlaylistIndex = index,
            StartAt = startAt,
            IsMusicDomain = isMusic,
        };
        return true;
    }

    private static bool TryBuildPlaylist(
        string? listId, string original, bool isMusic, [NotNullWhen(true)] out MediaUrlInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(listId) || !PlaylistIdRegex().IsMatch(listId)) return false;

        var kind = ClassifyPlaylistId(listId);
        var useMusicHost = isMusic || kind == MediaUrlKind.Album;
        var canonical = useMusicHost
            ? $"https://music.youtube.com/playlist?list={listId}"
            : $"https://www.youtube.com/playlist?list={listId}";

        info = new MediaUrlInfo
        {
            Kind = kind,
            OriginalUrl = original,
            CanonicalUrl = canonical,
            PlaylistId = listId,
            IsMusicDomain = useMusicHost,
        };
        return true;
    }

    /// <summary>
    /// Playlist id prefixes carry meaning: OLAK5uy_/MPREb are Music albums, RD/UL are generated
    /// radio mixes (effectively unbounded), everything else behaves as a normal playlist.
    /// </summary>
    internal static MediaUrlKind ClassifyPlaylistId(string listId)
    {
        if (listId.StartsWith("OLAK5uy_", StringComparison.Ordinal) ||
            listId.StartsWith("MPREb", StringComparison.Ordinal))
            return MediaUrlKind.Album;

        if (listId.StartsWith("RD", StringComparison.Ordinal) ||
            listId.StartsWith("UL", StringComparison.Ordinal))
            return MediaUrlKind.Mix;

        return MediaUrlKind.Playlist;
    }

    private static string BuildChannelUrl(string basePath, string[] segments, out ChannelTab tab)
    {
        // Only the trailing segment can be a tab, and only when it names one:
        // /channel/UC.../videos has a tab, /channel/UC... does not.
        var tabSegment = segments.Length > 1 ? segments[^1].ToLowerInvariant() : null;

        tab = tabSegment switch
        {
            "videos" => ChannelTab.Videos,
            "shorts" => ChannelTab.Shorts,
            "streams" or "live" => ChannelTab.Streams,
            "playlists" => ChannelTab.Playlists,
            "releases" => ChannelTab.Releases,
            "podcasts" => ChannelTab.Podcasts,
            _ => ChannelTab.Default,
        };

        var suffix = tab switch
        {
            ChannelTab.Videos => "/videos",
            ChannelTab.Shorts => "/shorts",
            ChannelTab.Streams => "/streams",
            ChannelTab.Playlists => "/playlists",
            ChannelTab.Releases => "/releases",
            ChannelTab.Podcasts => "/podcasts",
            _ => string.Empty,
        };
        return $"https://www.youtube.com/{basePath}{suffix}";
    }

    private static string? GetPlaylist(IReadOnlyDictionary<string, string> query)
    {
        var list = query.GetValueOrDefault("list");
        return !string.IsNullOrWhiteSpace(list) && PlaylistIdRegex().IsMatch(list) ? list : null;
    }

    private static int? GetIndex(IReadOnlyDictionary<string, string> query)
        => int.TryParse(query.GetValueOrDefault("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i > 0
            ? i
            : null;

    /// <summary>Reads ?t= / ?start= / #t=, accepting both "90" and "1h2m3s" forms.</summary>
    private static TimeSpan? ParseStartTime(IReadOnlyDictionary<string, string> query, string fragment)
    {
        var value = query.GetValueOrDefault("t") ?? query.GetValueOrDefault("start");
        if (string.IsNullOrEmpty(value) && fragment.StartsWith("#t=", StringComparison.OrdinalIgnoreCase))
            value = fragment[3..];
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Plain seconds, sometimes written as "90s" by the share dialog.
        var numeric = value.EndsWith('s') && value.Length > 1 && value[..^1].All(char.IsAsciiDigit)
            ? value[..^1]
            : value;
        if (int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

        var m = ClockTimeRegex().Match(value);
        if (!m.Success || !(m.Groups[1].Success || m.Groups[2].Success || m.Groups[3].Success)) return null;

        var h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var mi = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var s = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        var total = new TimeSpan(h, mi, s);
        return total > TimeSpan.Zero ? total : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }
}

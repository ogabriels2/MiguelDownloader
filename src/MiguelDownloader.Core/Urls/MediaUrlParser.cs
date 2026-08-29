using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MiguelDownloader.Core.Urls;

/// <summary>
/// The front door for every link the user supplies, whatever site it came from.
/// <para>
/// YouTube keeps its own reader: its URL shapes are finite, documented and worth reading closely,
/// because knowing a link is a mix rather than a playlist changes what the app does before it
/// spends a request. Other sites are classified from their path with the modest confidence that
/// deserves, and anything unrecognised is still passed through.
/// </para>
/// <para>
/// Refusing an unfamiliar host would be the wrong trade. The extractor ships with over 1700 site
/// handlers and gains more between our releases; a hard-coded allow-list here would turn working
/// links into "unsupported" purely because this file had not been updated. So the only links
/// rejected outright are the ones that are not links at all.
/// </para>
/// </summary>
public static partial class MediaUrlParser
{
    // Finds the first http(s) URL inside arbitrary text (clipboard, drag and drop payloads).
    [GeneratedRegex("https?://[^\\s<>\"']+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UrlInTextRegex();

    /// <summary>
    /// Campaign parameters that mean the same thing everywhere and are safe to drop on any site.
    /// Anything whose meaning depends on the site lives in that site's descriptor instead.
    /// </summary>
    private static readonly string[] UniversalTrackingParameters =
    [
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_name",
        "fbclid", "gclid", "msclkid",
    ];

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

        // A scheme-less host is normal when a link is copied out of a chat message, and it happens
        // on every site, not only on youtu.be.
        foreach (var descriptor in ProviderCatalog.All)
        {
            foreach (var host in descriptor.Hosts)
            {
                var idx = trimmed.IndexOf(host, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                if (idx > 0 && char.IsLetterOrDigit(trimmed[idx - 1])) continue;

                var candidate = trimmed[idx..].Split(' ', '\n', '\r', '\t')[0];
                // "youtube.com" alone is a home page, not a link to anything.
                if (candidate.Contains('/', StringComparison.Ordinal))
                    return "https://" + candidate;
            }
        }
        return null;
    }

    /// <summary>True when the text contains something worth handing to the extractor.</summary>
    public static bool LooksLikeMediaUrl(string? text)
        => TryParse(ExtractFirstUrl(text) ?? text ?? string.Empty, out var info) && info.IsDownloadable;

    public static bool TryParse(string? input, [NotNullWhen(true)] out MediaUrlInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var original = input.Trim();
        var raw = original.Trim('"', '\'', '<', '>');
        if (raw.Length == 0) return false;

        if (!raw.Contains("://", StringComparison.Ordinal))
            raw = "https://" + raw.TrimStart('/');

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;

        var descriptor = ProviderCatalog.ForHost(uri.Host);

        // YouTube's reader already produces everything below and more, so it stays authoritative
        // for its own hosts; all that is added here is the provider stamp.
        if (descriptor.Provider is MediaProvider.YouTube or MediaProvider.YouTubeMusic)
        {
            if (!YouTubeUrlReader.TryParse(original, out var youtube)) return false;
            info = youtube with
            {
                Provider = youtube.IsMusicDomain ? MediaProvider.YouTubeMusic : MediaProvider.YouTube,
            };
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kind = Classify(descriptor, segments, out var handle, out var itemId);

        info = new MediaUrlInfo
        {
            Provider = descriptor.Provider,
            Kind = kind,
            CanonicalUrl = Canonicalize(uri, descriptor),
            OriginalUrl = original,
            VideoId = itemId,
            Handle = handle,
        };
        return true;
    }

    /// <summary>
    /// Decides what a non-YouTube path points at, from the segments the site uses.
    /// <para>
    /// The bar here is deliberately low. Getting this wrong costs a missing "this will expand to
    /// many items" warning, which the analysis step then supplies anyway; getting it wrong in the
    /// other direction — refusing the link — costs the download. So anything unfamiliar returns
    /// <see cref="MediaUrlKind.Unknown"/>, which stays downloadable.
    /// </para>
    /// </summary>
    private static MediaUrlKind Classify(
        ProviderDescriptor descriptor, string[] segments, out string? handle, out string? itemId)
    {
        handle = null;
        itemId = null;
        if (segments.Length == 0) return MediaUrlKind.Unknown;

        // A leading @name is the profile convention on TikTok, Bluesky, Mastodon and others.
        var start = 0;
        if (segments[0].StartsWith('@'))
        {
            handle = segments[0][1..];
            start = 1;
        }

        // "/@someone" and nothing else is a person, which expands to everything they posted.
        if (start == 1 && segments.Length == 1) return MediaUrlKind.Profile;

        // Item segments are searched first, across the whole path, because an item inside a
        // collection is still one item: /r/videos/comments/abc is a single post, and matching
        // "r" before reaching "comments" would queue an entire subreddit instead.
        for (var i = start; i < segments.Length; i++)
        {
            var segment = segments[i].ToLowerInvariant();
            if (!descriptor.ItemSegments.Contains(segment, StringComparer.Ordinal)) continue;

            itemId = i + 1 < segments.Length ? segments[i + 1] : null;
            return ItemKindFor(descriptor.Provider, segment);
        }

        for (var i = start; i < segments.Length; i++)
        {
            var segment = segments[i].ToLowerInvariant();
            if (descriptor.CollectionSegments.Contains(segment, StringComparer.Ordinal))
                return CollectionKindFor(descriptor.Provider, segment);
        }

        // A bare /name on a site that has no matching segment is most likely that person's page.
        if (handle is not null) return MediaUrlKind.Profile;

        return MediaUrlKind.Unknown;
    }

    private static MediaUrlKind ItemKindFor(MediaProvider provider, string segment) => segment switch
    {
        "reel" or "reels" or "video" when provider is MediaProvider.Instagram or MediaProvider.Facebook
            => MediaUrlKind.Short,
        "video" or "photo" when provider is MediaProvider.TikTok => MediaUrlKind.Short,
        "spotlight" => MediaUrlKind.Short,
        "stories" => MediaUrlKind.Story,
        "status" or "statuses" or "post" or "posts" or "comments" or "feed" or "p" or "pin"
            => MediaUrlKind.Post,
        _ => MediaUrlKind.Video,
    };

    private static MediaUrlKind CollectionKindFor(MediaProvider provider, string segment) => segment switch
    {
        "album" or "albums" => MediaUrlKind.Album,
        "sets" or "playlist" or "showcase" or "collection" or "collections" => MediaUrlKind.Playlist,
        "stories" => MediaUrlKind.Story,
        "profile" or "user" or "users" or "c" => MediaUrlKind.Profile,
        _ => MediaUrlKind.Profile,
    };

    /// <summary>
    /// Rebuilds the URL without the parameters that describe the referrer rather than the content.
    /// Everything unrecognised is kept: a stripped parameter that mattered would break the link,
    /// which is a far worse outcome than a tidy one that carries a stray id.
    /// </summary>
    private static string Canonicalize(Uri uri, ProviderDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(uri.Query) || uri.Query == "?")
            return uri.GetLeftPart(UriPartial.Path);

        var kept = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (UniversalTrackingParameters.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (descriptor.TrackingParameters.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            kept.Add(pair);
        }

        var path = uri.GetLeftPart(UriPartial.Path);
        return kept.Count == 0 ? path : path + "?" + string.Join('&', kept);
    }
}

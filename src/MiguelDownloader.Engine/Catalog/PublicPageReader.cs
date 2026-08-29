using System.Net.Http;
using System.Text.RegularExpressions;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>What a platform's own page says about a release, when that is all it will say.</summary>
public sealed record ReleaseIdentity(string Title, string? Artist, string? CoverArtUrl)
{
    public bool IsUsable => Title.Length > 1;
}

/// <summary>
/// Reads the little that Spotify, TIDAL and Amazon Music publish anonymously.
/// <para>
/// These three render their catalogue in the browser and answer a plain request with a shell, so
/// there is no track list to read. What they do publish is the release's name and its artist,
/// which is enough to identify it -- and identification is all that is needed, because the track
/// list can then be read from a catalogue that is open.
/// </para>
/// </summary>
public sealed partial class PublicPageReader(HttpClient http, ILogger<PublicPageReader> logger)
{
    // Spotify's embed carries the release as "title" and the artist as "subtitle".
    [GeneratedRegex(@"""title""\s*:\s*""((?:[^""\\]|\\.){1,200})""", RegexOptions.CultureInvariant)]
    private static partial Regex EmbedTitleRegex();

    [GeneratedRegex(@"""subtitle""\s*:\s*""((?:[^""\\]|\\.){0,200})""", RegexOptions.CultureInvariant)]
    private static partial Regex EmbedSubtitleRegex();

    [GeneratedRegex(
        @"<meta[^>]+property=""og:(title|description|image)""[^>]+content=""([^""]*)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OpenGraphRegex();

    private readonly HttpClient _http = http;
    private readonly ILogger<PublicPageReader> _logger = logger;

    public async Task<ReleaseIdentity?> ReadAsync(
        MediaUrlInfo url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        return url.Provider switch
        {
            MediaProvider.Spotify => await ReadSpotifyAsync(url, cancellationToken).ConfigureAwait(false),
            _ => await ReadOpenGraphAsync(url.CanonicalUrl, cancellationToken).ConfigureAwait(false),
        };
    }

    private async Task<ReleaseIdentity?> ReadSpotifyAsync(
        MediaUrlInfo url, CancellationToken cancellationToken)
    {
        var embed = ToEmbedUrl(url.CanonicalUrl);
        if (embed is null) return null;

        var html = await GetStringAsync(embed, cancellationToken).ConfigureAwait(false);
        if (html is null) return null;

        var title = Unescape(EmbedTitleRegex().Match(html).Groups[1].Value);
        var subtitle = Unescape(EmbedSubtitleRegex().Match(html).Groups[1].Value);

        if (string.IsNullOrWhiteSpace(title))
        {
            _logger.LogInformation("The Spotify embed did not name the release");
            return null;
        }

        // A playlist's subtitle is its curator, not an artist, and using it to search would drag
        // the lookup off course. Only tracks and albums carry a real artist here.
        var isPlaylist = url.CanonicalUrl.Contains("/playlist/", StringComparison.OrdinalIgnoreCase);
        var artist = isPlaylist || string.IsNullOrWhiteSpace(subtitle) ? null : subtitle;

        return new ReleaseIdentity(title, artist, null);
    }

    private async Task<ReleaseIdentity?> ReadOpenGraphAsync(string url, CancellationToken cancellationToken)
    {
        var html = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        if (html is null) return null;

        string? title = null, description = null, image = null;
        foreach (Match m in OpenGraphRegex().Matches(html))
        {
            switch (m.Groups[1].Value.ToLowerInvariant())
            {
                case "title": title ??= m.Groups[2].Value; break;
                case "description": description ??= m.Groups[2].Value; break;
                case "image": image ??= m.Groups[2].Value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(title)) return null;

        // A site serving its own name rather than the release has told us nothing: TIDAL answers
        // "TIDAL - High Fidelity Music Streaming" to any request it has not rendered.
        if (LooksLikeSiteName(title))
        {
            _logger.LogInformation("The page returned its own name rather than the release");
            return null;
        }

        // These pages usually phrase it as "Album by Artist" or "Artist - Album".
        var (cleanTitle, artist) = SplitTitleAndArtist(title, description);
        return new ReleaseIdentity(cleanTitle, artist, image);
    }

    internal static bool LooksLikeSiteName(string title)
    {
        var t = title.ToLowerInvariant();
        return t.Contains("music streaming", StringComparison.Ordinal)
            || t.Contains("high fidelity", StringComparison.Ordinal)
            || t.StartsWith("amazon music", StringComparison.Ordinal)
            || t is "spotify" or "tidal" or "deezer";
    }

    /// <summary>
    /// Pulls a release name and an artist out of the one string these pages provide.
    /// </summary>
    internal static (string Title, string? Artist) SplitTitleAndArtist(string title, string? description)
    {
        // "Album by Artist on Apple Music" / "Song by Artist"
        var by = Regex.Match(title, @"^(?<t>.+?)\s+by\s+(?<a>.+?)(?:\s+on\s+\w[\w ]*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (by.Success) return (by.Groups["t"].Value.Trim(), by.Groups["a"].Value.Trim());

        // "Artist - Album"
        var dash = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (dash.Length == 2 && dash[0].Length > 0 && dash[1].Length > 0)
            return (dash[1], dash[0]);

        // Some pages leave the artist to the description: "Listen to X on ... by ARTIST".
        if (description is { Length: > 0 })
        {
            var fromDescription = Regex.Match(description, @"\bby\s+(?<a>[^.,;|]+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (fromDescription.Success)
                return (title.Trim(), fromDescription.Groups["a"].Value.Trim());
        }

        return (title.Trim(), null);
    }

    internal static string? ToEmbedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return null;

        // Already an embed link.
        if (segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
            return $"https://open.spotify.com{uri.AbsolutePath}";

        var kind = segments[^2].ToLowerInvariant();
        var id = segments[^1];
        return kind is "track" or "album" or "playlist" or "artist" or "show" or "episode"
            ? $"https://open.spotify.com/embed/{kind}/{id}"
            : null;
    }

    private static string Unescape(string value)
        => value.Replace("\\/", "/", StringComparison.Ordinal)
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\u0026", "&", StringComparison.Ordinal)
                .Trim();

    private async Task<string?> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // These pages serve a different, emptier body to clients they do not recognise.
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/120.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogInformation(ex, "Could not read {Url}", url);
            return null;
        }
    }
}

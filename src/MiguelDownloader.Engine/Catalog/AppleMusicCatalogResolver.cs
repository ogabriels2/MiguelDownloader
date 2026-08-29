using System.Net.Http;
using System.Text.Json;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>
/// Reads Apple's public catalogue through the iTunes lookup service.
/// <para>
/// The endpoint needs no key and returns the whole track list for a release, with positions,
/// genre, copyright and durations. It is the same catalogue Apple Music serves; only the audio
/// is withheld, and that is FairPlay-encrypted and not something this application fetches.
/// </para>
/// <para>
/// Playlists are the gap: Apple's editorial playlists carry ids of the form <c>pl.xxxxx</c> that
/// the lookup service does not answer for. Those fall back to the release title read from the
/// page, and are looked up elsewhere.
/// </para>
/// </summary>
public sealed class AppleMusicCatalogResolver(HttpClient http, ILogger<AppleMusicCatalogResolver> logger)
{
    private const string LookupRoot = "https://itunes.apple.com/lookup";

    private readonly HttpClient _http = http;
    private readonly ILogger<AppleMusicCatalogResolver> _logger = logger;

    public async Task<MusicCatalog?> ResolveAsync(
        MediaUrlInfo url, int? limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (kind, id, trackId) = IdentifyPath(url.OriginalUrl);
        if (id is null)
        {
            _logger.LogInformation("Apple Music link carried no numeric catalogue id");
            return null;
        }

        // "?i=" means one track inside an album page: the album is fetched and the track picked
        // out of it, which keeps the album context the file should be tagged with.
        var wanted = trackId ?? (kind == CatalogKind.Track ? id : null);
        var lookupId = kind == CatalogKind.Artist ? id : (trackId is not null ? id : id);

        var entity = kind == CatalogKind.Artist ? "song" : "song";
        var take = Math.Min(limit ?? 200, 200);
        var json = await GetAsync(
            $"{LookupRoot}?id={Uri.EscapeDataString(lookupId)}&entity={entity}&limit={take}",
            cancellationToken).ConfigureAwait(false);

        if (json is null) return null;
        if (!json.Value.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            _logger.LogInformation("Apple's catalogue returned nothing for {Id}", lookupId);
            return null;
        }

        JsonElement? collection = null;
        var songs = new List<JsonElement>();

        foreach (var entry in results.EnumerateArray())
        {
            var wrapper = entry.String("wrapperType");
            if (wrapper is "collection" or "artist") collection ??= entry;
            else if (wrapper == "track") songs.Add(entry);
        }

        var albumTitle = collection?.String("collectionName");
        var albumArtist = collection?.String("artistName");
        var cover = UpscaleArtwork(collection?.String("artworkUrl100") ?? songs.FirstOrDefault().String("artworkUrl100"));
        var copyright = collection?.String("copyright");
        var releaseDate = collection?.String("releaseDate") ?? songs.FirstOrDefault().String("releaseDate");
        var total = collection?.Int("trackCount") ?? songs.Count;

        var tracks = songs
            .Where(s => wanted is null || s.Id() == wanted)
            .Select(s => MapTrack(s, albumTitle, albumArtist, cover, copyright, total))
            .ToList();

        if (tracks.Count == 0) return null;

        return new MusicCatalog
        {
            Kind = wanted is not null ? CatalogKind.Track
                 : kind == CatalogKind.Artist ? CatalogKind.Artist
                 : CatalogKind.Album,
            Title = wanted is not null ? tracks[0].Title : albumTitle ?? tracks[0].Album ?? "?",
            Artist = albumArtist ?? tracks[0].Artist,
            Platform = MusicPlatform.AppleMusic,
            CoverArtUrl = cover,
            ReleaseDate = releaseDate,
            PlatformUrl = url.CanonicalUrl,
            Tracks = tracks,
            DeclaredCount = wanted is not null ? 1 : total,
        };
    }

    private static CatalogTrack MapTrack(
        JsonElement s, string? album, string? albumArtist, string? cover, string? copyright, int? total)
        => new()
        {
            Title = s.String("trackName") ?? "?",
            Artist = s.String("artistName") ?? albumArtist ?? "?",
            AlbumArtist = albumArtist,
            Album = s.String("collectionName") ?? album,
            TrackNumber = s.Int("trackNumber"),
            TrackTotal = s.Int("trackCount") ?? total,
            DiscNumber = s.Int("discNumber"),
            DiscTotal = s.Int("discCount"),
            Duration = s.Int("trackTimeMillis") is { } ms and > 0
                ? TimeSpan.FromMilliseconds(ms)
                : null,
            Genre = s.String("primaryGenreName"),
            ReleaseDate = s.String("releaseDate"),
            Copyright = copyright,
            Composer = s.String("composerName"),
            CoverArtUrl = UpscaleArtwork(s.String("artworkUrl100")) ?? cover,
            PlatformUrl = s.String("trackViewUrl"),
            Platform = MusicPlatform.AppleMusic,
        };

    /// <summary>
    /// Apple returns a 100px thumbnail and encodes the size in the file name. Asking for a larger
    /// one is a substitution, not an upscale: the bigger file genuinely exists on their servers.
    /// </summary>
    internal static string? UpscaleArtwork(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : url.Replace("100x100bb", "1400x1400bb", StringComparison.Ordinal);

    /// <summary>
    /// Reads what an Apple Music path points at.
    /// <para>
    /// The shape is /{country}/{kind}/{slug}/{id}, with an optional ?i= naming one track inside
    /// an album. The slug is decorative and ignored.
    /// </para>
    /// </summary>
    internal static (CatalogKind Kind, string? Id, string? TrackId) IdentifyPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (CatalogKind.Unknown, null, null);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kind = CatalogKind.Unknown;

        foreach (var segment in segments)
        {
            kind = segment.ToLowerInvariant() switch
            {
                "album" => CatalogKind.Album,
                "song" => CatalogKind.Track,
                "artist" => CatalogKind.Artist,
                "playlist" => CatalogKind.Playlist,
                _ => kind,
            };
        }

        // The id is the last all-digit segment. Editorial playlists use "pl.xxxx" instead, which
        // the lookup service does not answer for; those return no id and get bridged.
        var id = segments.LastOrDefault(s => s.Length > 0 && s.All(char.IsDigit));

        string? trackId = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("i", StringComparison.OrdinalIgnoreCase))
                trackId = parts[1];
        }

        return (kind, id, trackId);
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Could not read Apple's catalogue");
            return null;
        }
    }
}

using System.Net.Http;
using System.Text.Json;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>
/// Reads Deezer's public catalogue.
/// <para>
/// api.deezer.com answers without a key, and answers well: albums and playlists come back with
/// their whole track list, and each track carries its ISRC, its position on the disc, the label,
/// the barcode and cover art at 1000x1000. That makes Deezer the reference catalogue here, and
/// the place a link from a platform that publishes less gets looked up.
/// </para>
/// <para>
/// Audio is not fetched from Deezer and cannot be: its streams are Blowfish-encrypted, and the
/// tools that decrypt them do so with a reverse-engineered secret key. This reads the catalogue
/// only, which is public.
/// </para>
/// </summary>
public sealed class DeezerCatalogResolver(HttpClient http, ILogger<DeezerCatalogResolver> logger)
{
    private const string ApiRoot = "https://api.deezer.com";

    /// <summary>
    /// How many tracks one request returns. Deezer caps this itself, so a large playlist is read
    /// page by page until it is complete or the caller's limit is reached.
    /// </summary>
    public const int PageSize = 100;

    private readonly HttpClient _http = http;
    private readonly ILogger<DeezerCatalogResolver> _logger = logger;

    /// <summary>Reads whatever a Deezer link points at.</summary>
    public async Task<MusicCatalog?> ResolveAsync(
        MediaUrlInfo url, int? limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var (kind, id) = IdentifyPath(url.CanonicalUrl);
        if (kind == CatalogKind.Unknown || id is null)
        {
            _logger.LogInformation("Deezer link did not name a track, album, playlist or artist");
            return null;
        }

        return kind switch
        {
            CatalogKind.Track => await ResolveTrackAsync(id, cancellationToken).ConfigureAwait(false),
            CatalogKind.Album => await ResolveAlbumAsync(id, cancellationToken).ConfigureAwait(false),
            CatalogKind.Playlist => await ResolvePlaylistAsync(id, limit, cancellationToken).ConfigureAwait(false),
            CatalogKind.Artist => await ResolveArtistAsync(id, limit, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    /// <summary>
    /// Finds a release by name, used when the link came from a platform that publishes only a
    /// title. Returns null rather than a doubtful match: presenting the wrong album confidently
    /// is worse than admitting the lookup failed.
    /// </summary>
    public async Task<MusicCatalog?> FindAlbumAsync(
        string title, string? artist, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        var found = await GetAsync($"{ApiRoot}/search/album?q={Uri.EscapeDataString(query)}&limit=5",
            cancellationToken).ConfigureAwait(false);

        if (found is null || !found.Value.TryGetProperty("data", out var data)) return null;

        foreach (var candidate in data.EnumerateArray())
        {
            var candidateTitle = candidate.String("title");
            var candidateArtist = candidate.TryGetProperty("artist", out var a) ? a.String("name") : null;

            if (!TitlesAgree(title, candidateTitle)) continue;
            if (artist is { Length: > 0 } && !TitlesAgree(artist, candidateArtist)) continue;

            var id = candidate.Id();
            if (id is null) continue;

            var album = await ResolveAlbumAsync(id, cancellationToken).ConfigureAwait(false);
            if (album is not null) return album with { WasBridged = true, BridgedVia = MusicPlatform.Deezer };
        }

        _logger.LogInformation("No Deezer album matched {Title} by {Artist} confidently", title, artist);
        return null;
    }

    /// <summary>Finds one recording by name, for a bridged track link.</summary>
    public async Task<CatalogTrack?> FindTrackAsync(
        string title, string? artist, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
        var found = await GetAsync($"{ApiRoot}/search/track?q={Uri.EscapeDataString(query)}&limit=5",
            cancellationToken).ConfigureAwait(false);

        if (found is null || !found.Value.TryGetProperty("data", out var data)) return null;

        foreach (var candidate in data.EnumerateArray())
        {
            if (!TitlesAgree(title, candidate.String("title"))) continue;

            var id = candidate.Id();
            if (id is null) continue;

            var detail = await GetAsync($"{ApiRoot}/track/{id}", cancellationToken).ConfigureAwait(false);
            if (detail is not null) return MapTrack(detail.Value, null);
        }
        return null;
    }

    private async Task<MusicCatalog?> ResolveTrackAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{ApiRoot}/track/{id}", cancellationToken).ConfigureAwait(false);
        if (json is null) return null;

        var track = MapTrack(json.Value, null);
        return new MusicCatalog
        {
            Kind = CatalogKind.Track,
            Title = track.Title,
            Artist = track.Artist,
            Platform = MusicPlatform.Deezer,
            CoverArtUrl = track.CoverArtUrl,
            ReleaseDate = track.ReleaseDate,
            PlatformUrl = json.Value.String("link"),
            Tracks = [track],
            DeclaredCount = 1,
        };
    }

    private async Task<MusicCatalog?> ResolveAlbumAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{ApiRoot}/album/{id}", cancellationToken).ConfigureAwait(false);
        if (json is null) return null;

        var album = json.Value;
        var albumArtist = album.TryGetProperty("artist", out var ar) ? ar.String("name") : null;
        var cover = album.String("cover_xl") ?? album.String("cover_big") ?? album.String("cover");
        var releaseDate = album.String("release_date");
        var label = album.String("label");
        var barcode = album.String("upc");
        var genre = album.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd)
            && gd.ValueKind == JsonValueKind.Array && gd.GetArrayLength() > 0
            ? gd[0].String("name")
            : null;

        var tracks = new List<CatalogTrack>();
        if (album.TryGetProperty("tracks", out var t) && t.TryGetProperty("data", out var list))
        {
            var total = list.GetArrayLength();
            var position = 0;
            foreach (var entry in list.EnumerateArray())
            {
                position++;
                // The album payload omits the ISRC, so each track is fetched individually. It is
                // the field that makes a match verifiable, and worth one request apiece.
                var detail = entry.Id() is { } trackId
                    ? await GetAsync($"{ApiRoot}/track/{trackId}", cancellationToken).ConfigureAwait(false)
                    : null;

                var source = detail ?? entry;
                tracks.Add(MapTrack(source, new AlbumContext(
                    album.String("title"), albumArtist, cover, releaseDate, label, barcode, genre, total, position)));
            }
        }

        return new MusicCatalog
        {
            Kind = CatalogKind.Album,
            Title = album.String("title") ?? "?",
            Artist = albumArtist,
            Platform = MusicPlatform.Deezer,
            CoverArtUrl = cover,
            ReleaseDate = releaseDate,
            Label = label,
            Barcode = barcode,
            PlatformUrl = album.String("link"),
            Tracks = tracks,
            DeclaredCount = album.Int("nb_tracks") ?? tracks.Count,
        };
    }

    private async Task<MusicCatalog?> ResolvePlaylistAsync(
        string id, int? limit, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"{ApiRoot}/playlist/{id}", cancellationToken).ConfigureAwait(false);
        if (json is null) return null;

        var playlist = json.Value;
        var tracks = new List<CatalogTrack>();

        // The first payload can already hold more than the caller asked for, so the ceiling is
        // applied here as well as to the pages that follow.
        var declared = playlist.Int("nb_tracks") ?? 0;
        var wanted = limit is > 0 ? limit.Value : int.MaxValue;

        if (playlist.TryGetProperty("tracks", out var t) && t.TryGetProperty("data", out var list))
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (tracks.Count >= wanted) break;
                tracks.Add(MapTrack(entry, null));
            }
        }

        if (declared == 0) declared = tracks.Count;
        wanted = Math.Min(wanted, declared);

        // The payload carries only the first page. Everything beyond it has to be asked for, and a
        // playlist of a thousand tracks is an ordinary thing to paste -- stopping at the first
        // page would silently hand back a tenth of it.

        while (tracks.Count < wanted)
        {
            var page = await GetAsync(
                $"{ApiRoot}/playlist/{id}/tracks?index={tracks.Count}&limit={PageSize}",
                cancellationToken).ConfigureAwait(false);

            if (page is null || !page.Value.TryGetProperty("data", out var more)
                || more.ValueKind != JsonValueKind.Array || more.GetArrayLength() == 0)
            {
                // The source stopped supplying: report what actually arrived rather than looping.
                break;
            }

            foreach (var entry in more.EnumerateArray())
            {
                if (tracks.Count >= wanted) break;
                tracks.Add(MapTrack(entry, null));
            }

            if (cancellationToken.IsCancellationRequested) break;
        }

        _logger.LogInformation(
            "Deezer playlist {Id}: {Count} of {Declared} track(s)", id, tracks.Count, declared);

        return new MusicCatalog
        {
            Kind = CatalogKind.Playlist,
            Title = playlist.String("title") ?? "?",
            Artist = playlist.TryGetProperty("creator", out var c) ? c.String("name") : null,
            Platform = MusicPlatform.Deezer,
            CoverArtUrl = playlist.String("picture_xl") ?? playlist.String("picture_big"),
            PlatformUrl = playlist.String("link"),
            Tracks = tracks,
            DeclaredCount = playlist.Int("nb_tracks") ?? tracks.Count,
        };
    }

    private async Task<MusicCatalog?> ResolveArtistAsync(
        string id, int? limit, CancellationToken cancellationToken)
    {
        var artist = await GetAsync($"{ApiRoot}/artist/{id}", cancellationToken).ConfigureAwait(false);
        if (artist is null) return null;

        // An artist link means their catalogue; the top tracks are what the platform itself
        // presents first, and expanding a whole discography is a separate, much larger request.
        var wanted = limit is > 0 ? limit.Value : 200;
        var tracks = new List<CatalogTrack>();

        while (tracks.Count < wanted)
        {
            var take = Math.Min(PageSize, wanted - tracks.Count);
            var top = await GetAsync(
                $"{ApiRoot}/artist/{id}/top?index={tracks.Count}&limit={take}", cancellationToken)
                .ConfigureAwait(false);

            if (top is null || !top.Value.TryGetProperty("data", out var list)
                || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var entry in list.EnumerateArray()) tracks.Add(MapTrack(entry, null));
            if (cancellationToken.IsCancellationRequested) break;
        }

        return new MusicCatalog
        {
            Kind = CatalogKind.Artist,
            Title = artist.Value.String("name") ?? "?",
            Artist = artist.Value.String("name"),
            Platform = MusicPlatform.Deezer,
            CoverArtUrl = artist.Value.String("picture_xl") ?? artist.Value.String("picture_big"),
            PlatformUrl = artist.Value.String("link"),
            Tracks = tracks,
            DeclaredCount = tracks.Count,
        };
    }

    private readonly record struct AlbumContext(
        string? Album, string? AlbumArtist, string? Cover, string? ReleaseDate,
        string? Label, string? Barcode, string? Genre, int? Total, int? Position);

    private static CatalogTrack MapTrack(JsonElement t, AlbumContext? album)
    {
        var nestedAlbum = t.TryGetProperty("album", out var a) ? a : default;
        var artist = t.TryGetProperty("artist", out var ar) ? ar.String("name") : null;

        var cover = album?.Cover
            ?? (nestedAlbum.ValueKind == JsonValueKind.Object
                ? nestedAlbum.String("cover_xl") ?? nestedAlbum.String("cover_big")
                : null);

        var gain = t.Double("gain");

        return new CatalogTrack
        {
            Title = t.String("title_short") ?? t.String("title") ?? "?",
            Artist = artist ?? album?.AlbumArtist ?? "?",
            AlbumArtist = album?.AlbumArtist ?? artist,
            Album = album?.Album ?? (nestedAlbum.ValueKind == JsonValueKind.Object ? nestedAlbum.String("title") : null),
            TrackNumber = t.Int("track_position") ?? album?.Position,
            TrackTotal = album?.Total,
            DiscNumber = t.Int("disk_number"),
            Duration = t.Int("duration") is { } seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null,
            ReleaseDate = t.String("release_date") ?? album?.ReleaseDate,
            Label = album?.Label,
            Genre = album?.Genre,
            Isrc = t.String("isrc"),
            Barcode = album?.Barcode,
            // Deezer reports 0 for tracks it has not analysed; that is "unmeasured", not "zero BPM".
            BeatsPerMinute = t.Double("bpm") is { } bpm and > 0 ? bpm : null,
            ReplayGainTrackGain = gain is { } value && Math.Abs(value) > 0.0001 ? value : null,
            CoverArtUrl = cover,
            PlatformUrl = t.String("link"),
            Platform = MusicPlatform.Deezer,
        };
    }

    /// <summary>
    /// Reads what a Deezer path points at. Handles the country-prefixed form (/us/album/123) as
    /// well as the bare one, because both are what people paste.
    /// </summary>
    internal static (CatalogKind Kind, string? Id) IdentifyPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (CatalogKind.Unknown, null);

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var kind = segments[i].ToLowerInvariant() switch
            {
                "track" => CatalogKind.Track,
                "album" => CatalogKind.Album,
                "playlist" => CatalogKind.Playlist,
                "artist" => CatalogKind.Artist,
                _ => CatalogKind.Unknown,
            };
            if (kind == CatalogKind.Unknown) continue;

            var id = new string(segments[i + 1].TakeWhile(char.IsDigit).ToArray());
            return id.Length > 0 ? (kind, id) : (CatalogKind.Unknown, null);
        }
        return (CatalogKind.Unknown, null);
    }

    /// <summary>
    /// Compares two titles loosely enough to survive punctuation and edition suffixes, and
    /// strictly enough not to accept a different record.
    /// </summary>
    internal static bool TitlesAgree(string? wanted, string? candidate)
    {
        var a = Normalize(wanted);
        var b = Normalize(candidate);
        if (a.Length == 0 || b.Length == 0) return false;

        return a == b || a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) builder.Append(c);
            else if (c is ' ' or '-' or '_') builder.Append(' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<JsonElement?> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Deezer answered {Status} for {Url}", (int)response.StatusCode, url);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            // Deezer reports failures in the body with a 200, so the payload has to be checked.
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.Object)
            {
                _logger.LogInformation("Deezer returned an error payload: {Message}", error.String("message"));
                return null;
            }

            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogInformation(ex, "Could not read the Deezer catalogue");
            return null;
        }
    }
}

/// <summary>Small readers that keep the mapping above free of null checks.</summary>
internal static class JsonElementExtensions
{
    public static string? String(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    public static int? Int(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
                _ => null,
            }
            : null;

    public static double? Double(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                JsonValueKind.String when double.TryParse(
                    value.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => null,
            }
            : null;

    /// <summary>Deezer returns ids as numbers in some payloads and strings in others.</summary>
    public static string? Id(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }
}

using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Urls;

namespace MiguelDownloader.Core.Music;

/// <summary>
/// Presents a streaming catalogue through the same shape the rest of the application already
/// understands.
/// <para>
/// A catalogue and a playlist listed flat are the same thing from the interface's point of view:
/// an ordered set of items that carry a title, an artist and a length, and whose actual streams
/// are resolved later. Reusing <see cref="MediaCollection"/> means the track list, the selection,
/// the count and the bulk warning all work without a second implementation.
/// </para>
/// <para>
/// The items are stubs with no URL of their own, because at this point there is no URL: the
/// recording has not been located yet. It is found when the download is started, which is also
/// what keeps the analysis quick.
/// </para>
/// </summary>
public static class CatalogPresentation
{
    public static AnalysisResult ToAnalysisResult(MusicCatalog catalog, MediaUrlInfo source)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(source);

        var warnings = new List<string>();

        // Said plainly and every time: what was read is a catalogue, and the recordings will come
        // from somewhere else. Anything less would let the interface imply it is downloading from
        // Spotify, which it is not and cannot.
        warnings.Add("Warning_CatalogueOnly");

        if (catalog.WasBridged) warnings.Add("Warning_CatalogueBridged");
        if (!catalog.IsComplete) warnings.Add("Warning_CollectionTruncated");

        var items = catalog.Tracks.Select(ToItem).ToList();

        // A single track still becomes a collection of one rather than a plain item: the download
        // path for a catalogue is the same either way, and pretending otherwise would need two.
        var collection = new MediaCollection
        {
            Id = catalog.PlatformUrl ?? catalog.Title,
            Title = catalog.Title,
            WebpageUrl = catalog.PlatformUrl ?? string.Empty,
            Kind = catalog.Kind == CatalogKind.Album ? MediaKind.Album : MediaKind.Playlist,
            AlbumArtist = catalog.Artist,
            ChannelName = catalog.Artist,
            Uploader = catalog.Artist,
            Year = ParseYear(catalog.ReleaseDate),
            Items = items,
            DeclaredCount = catalog.DeclaredCount,
            Thumbnails = catalog.CoverArtUrl is { Length: > 0 } cover
                ? [new ThumbnailInfo { Url = cover }]
                : [],
        };

        return new AnalysisResult
        {
            Source = source,
            Collection = collection,
            Profile = ContentProfile.Music,
            Warnings = warnings,
        };
    }

    private static MediaItem ToItem(CatalogTrack track) => new()
    {
        // The ISRC is the natural identity here, and the only one that exists before a source is
        // located.
        Id = track.Isrc ?? $"{track.Artist}|{track.Title}",
        Title = track.Title,
        // Deliberately the platform's page: it is where this description came from, and it is not
        // where the audio will be fetched. The real source is filled in once one is found.
        WebpageUrl = track.PlatformUrl ?? string.Empty,
        ChannelName = track.Artist,
        Uploader = track.Artist,
        Duration = track.Duration,
        Kind = MediaKind.Music,
        Music = track.ToMetadata(),
        PlaylistIndex = track.TrackNumber,
        IsStub = true,
        Thumbnails = track.CoverArtUrl is { Length: > 0 } cover
            ? [new ThumbnailInfo { Url = cover }]
            : [],
    };

    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate)) return null;
        return int.TryParse(releaseDate.AsSpan(0, Math.Min(4, releaseDate.Length)), out var year)
            && year is > 1000 and < 3000
            ? year
            : null;
    }
}

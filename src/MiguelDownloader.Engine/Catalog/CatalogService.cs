using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>
/// Turns a link from a streaming platform into the list of recordings it names.
/// <para>
/// None of these platforms serve audio this application can fetch: Spotify, TIDAL and Amazon
/// Music encrypt it with Widevine, Apple Music with FairPlay, Deezer with Blowfish. What is
/// public is the catalogue -- what the release is, which recordings it contains, and the codes
/// that identify them -- and that is what this reads.
/// </para>
/// <para>
/// Where a platform publishes its catalogue openly, it is read directly. Where it does not, the
/// release is identified from its own page and the track list read from one that does. A bridged
/// result says so, because it is right nearly always and wrong occasionally, and the person
/// looking at it can tell instantly which.
/// </para>
/// </summary>
public sealed class CatalogService(
    DeezerCatalogResolver deezer,
    AppleMusicCatalogResolver apple,
    PublicPageReader pageReader,
    MusicBrainzEnricher musicBrainz,
    ILogger<CatalogService> logger)
{
    private readonly DeezerCatalogResolver _deezer = deezer;
    private readonly AppleMusicCatalogResolver _apple = apple;
    private readonly PublicPageReader _pageReader = pageReader;
    private readonly MusicBrainzEnricher _musicBrainz = musicBrainz;
    private readonly ILogger<CatalogService> _logger = logger;

    /// <summary>True when this link is one whose catalogue is read rather than downloaded.</summary>
    public static bool Handles(MediaUrlInfo url) => url?.IsEncryptedCatalogue == true;

    /// <summary>
    /// Resolves the link. Returns null when the catalogue could not be read, which the caller
    /// surfaces as a real failure rather than an empty result.
    /// </summary>
    /// <param name="enrich">
    /// Look each recording up in MusicBrainz by its ISRC. Adds canonical identifiers, at the cost
    /// of one rate-limited request per track, so it is worth it for an album and not for a
    /// hundred-track playlist.
    /// </param>
    public async Task<MusicCatalog?> ResolveAsync(
        MediaUrlInfo url,
        int? limit = null,
        bool enrich = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!Handles(url)) return null;

        var platform = url.MusicPlatform;
        _logger.LogInformation("Reading the {Platform} catalogue for {Kind}", platform, url.Kind);

        var catalog = platform switch
        {
            MusicPlatform.Deezer => await _deezer.ResolveAsync(url, limit, cancellationToken).ConfigureAwait(false),
            MusicPlatform.AppleMusic => await _apple.ResolveAsync(url, limit, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        // Apple's editorial playlists carry no numeric id, so a direct read finds nothing and the
        // bridge takes over exactly as it does for the platforms that publish nothing.
        catalog ??= await BridgeAsync(url, platform, cancellationToken).ConfigureAwait(false);

        if (catalog is null)
        {
            _logger.LogInformation("No catalogue could be read for this {Platform} link", platform);
            return null;
        }

        // The link is from the platform the user pasted, whatever was read to satisfy it.
        catalog = catalog with { Platform = platform, PlatformUrl = url.CanonicalUrl };

        if (enrich && catalog.Tracks.Count > 0)
        {
            var enriched = await _musicBrainz
                .EnrichAsync(catalog.Tracks, cancellationToken).ConfigureAwait(false);
            catalog = catalog with { Tracks = enriched };
        }

        _logger.LogInformation(
            "Resolved {Count} of {Declared} track(s) from {Platform}{Bridged}",
            catalog.Tracks.Count, catalog.DeclaredCount, platform,
            catalog.WasBridged ? $" via {catalog.BridgedVia}" : string.Empty);

        return catalog;
    }

    /// <summary>
    /// Identifies the release from its own page, then reads the track list from Deezer.
    /// </summary>
    private async Task<MusicCatalog?> BridgeAsync(
        MediaUrlInfo url, MusicPlatform platform, CancellationToken cancellationToken)
    {
        var identity = await _pageReader.ReadAsync(url, cancellationToken).ConfigureAwait(false);
        if (identity is null || !identity.IsUsable)
        {
            _logger.LogInformation("{Platform} published nothing that identifies the release", platform);
            return null;
        }

        _logger.LogInformation(
            "{Platform} names it {Title} by {Artist}; looking it up on Deezer",
            platform, identity.Title, identity.Artist ?? "(unstated)");

        // A track link wants one recording; anything else wants the release.
        if (url.Kind is MediaUrlKind.Video or MediaUrlKind.Post
            || url.CanonicalUrl.Contains("/track/", StringComparison.OrdinalIgnoreCase))
        {
            var track = await _deezer
                .FindTrackAsync(identity.Title, identity.Artist, cancellationToken).ConfigureAwait(false);

            if (track is null) return null;

            return new MusicCatalog
            {
                Kind = CatalogKind.Track,
                Title = track.Title,
                Artist = track.Artist,
                Platform = platform,
                CoverArtUrl = track.CoverArtUrl ?? identity.CoverArtUrl,
                Tracks = [track],
                DeclaredCount = 1,
                WasBridged = true,
                BridgedVia = MusicPlatform.Deezer,
            };
        }

        return await _deezer
            .FindAlbumAsync(identity.Title, identity.Artist, cancellationToken).ConfigureAwait(false);
    }
}

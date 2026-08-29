namespace MiguelDownloader.Core.Music;

/// <summary>A streaming platform whose catalogue can be read.</summary>
public enum MusicPlatform
{
    None = 0,
    Spotify,
    AppleMusic,
    Deezer,
    Tidal,
    AmazonMusic,
    YouTubeMusic,
    MusicBrainz,
}

/// <summary>
/// How completely a platform's catalogue can be read without an account.
/// </summary>
public enum CatalogReach
{
    /// <summary>Nothing is readable without credentials.</summary>
    None = 0,

    /// <summary>
    /// Only what the link's own page advertises: usually a title, an artist and a cover. Enough to
    /// identify the release and look it up elsewhere, not enough to list its tracks.
    /// </summary>
    Basic,

    /// <summary>Tracks, positions, identifiers and artwork, all from a public endpoint.</summary>
    Full,
}

/// <summary>
/// What each platform will and will not give up, measured rather than assumed.
/// </summary>
/// <remarks>
/// None of these platforms serve audio that this application can download. Every one of them
/// encrypts it -- Spotify, Tidal and Amazon Music with Widevine, Apple Music with FairPlay,
/// Deezer with Blowfish -- and the tools that do download from them work by breaking that
/// encryption with a leaked secret, a key dumped from a physical Android device, or a client
/// impersonating the official app. This application does not do that, so what it reads here is
/// the catalogue: the identity of each recording, which is public.
/// </remarks>
public static class MusicPlatformInfo
{
    public static string DisplayName(MusicPlatform platform) => platform switch
    {
        MusicPlatform.Spotify => "Spotify",
        MusicPlatform.AppleMusic => "Apple Music",
        MusicPlatform.Deezer => "Deezer",
        MusicPlatform.Tidal => "TIDAL",
        MusicPlatform.AmazonMusic => "Amazon Music",
        MusicPlatform.YouTubeMusic => "YouTube Music",
        MusicPlatform.MusicBrainz => "MusicBrainz",
        _ => string.Empty,
    };

    /// <summary>How much of the catalogue is readable anonymously. Measured, August 2026.</summary>
    public static CatalogReach Reach(MusicPlatform platform) => platform switch
    {
        // api.deezer.com serves albums, playlists, artists and per-track ISRCs with no key at all.
        MusicPlatform.Deezer => CatalogReach.Full,

        // itunes.apple.com/lookup returns the full track list, genre and copyright; the album page
        // carries JSON-LD as well.
        MusicPlatform.AppleMusic => CatalogReach.Full,

        // The embed page gives a title and the artist as its subtitle. The full catalogue needs a
        // free client id and secret, which the settings accept when the user supplies them.
        MusicPlatform.Spotify => CatalogReach.Basic,

        // Both render entirely in the browser and expose nothing useful to a plain request.
        MusicPlatform.Tidal => CatalogReach.Basic,
        MusicPlatform.AmazonMusic => CatalogReach.Basic,

        _ => CatalogReach.None,
    };

    /// <summary>
    /// True when a link from this platform has to be looked up elsewhere to get its track list.
    /// </summary>
    public static bool NeedsBridge(MusicPlatform platform) => Reach(platform) == CatalogReach.Basic;
}

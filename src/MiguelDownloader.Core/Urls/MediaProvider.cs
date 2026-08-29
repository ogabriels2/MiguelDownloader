namespace MiguelDownloader.Core.Urls;

/// <summary>The site a URL came from.</summary>
/// <remarks>
/// This exists to choose parsing and wording, never to make promises about what a site serves.
/// What a given item actually offers — HDR, several audio tracks, subtitles, a real file size —
/// is read from the extractor's answer for that item, because it varies per post on every one of
/// these sites and changes without notice.
/// </remarks>
public enum MediaProvider
{
    /// <summary>Recognised as a URL, but from no site we describe specially.</summary>
    Generic = 0,
    YouTube,
    YouTubeMusic,
    Instagram,
    TikTok,
    Twitter,
    Facebook,
    Reddit,
    Vimeo,
    Twitch,
    SoundCloud,
    Bluesky,
    Pinterest,
    Dailymotion,
    Snapchat,
    LinkedIn,
    Kick,
    Bandcamp,
    Rumble,
    Tumblr,
    Vk,
    Bilibili,
    Streamable,
    Odysee,
    Mastodon,

    // Streaming services whose audio is encrypted. A link from one of these is a catalogue
    // reference, not a download: see MusicPlatformInfo for what that means and why.
    Spotify,
    AppleMusic,
    Deezer,
    Tidal,
    AmazonMusic,
}

/// <summary>
/// How often a site asks for a session before it will serve anything.
/// <para>
/// A hint for wording only. It decides whether the interface offers the sign-in advice up front
/// instead of after a failure; it never blocks an attempt, because "usually" is not "always" and
/// the only way to find out for a given link is to ask.
/// </para>
/// </summary>
public enum SignInExpectation
{
    /// <summary>Public items generally work without a session.</summary>
    Rarely = 0,

    /// <summary>Plenty of public items work; a sizeable share still asks.</summary>
    Sometimes,

    /// <summary>Anonymous access is mostly closed; expect to need cookies.</summary>
    Usually,
}

/// <summary>Static, per-site facts used for parsing and wording.</summary>
public sealed record ProviderDescriptor
{
    public required MediaProvider Provider { get; init; }

    /// <summary>The site's own name, shown as-is and never translated.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Registrable domains. Matching is a suffix test, so www., m., mobile. and other
    /// subdomains resolve to the same provider without being listed.
    /// </summary>
    public required string[] Hosts { get; init; }

    public SignInExpectation SignIn { get; init; } = SignInExpectation.Rarely;

    /// <summary>
    /// True for sites whose content is audio. The workspace opens on the music flow for these
    /// rather than offering a video mode the source cannot satisfy.
    /// </summary>
    public bool IsAudioFirst { get; init; }

    /// <summary>
    /// First path segments that mean "a person or a collection" rather than one item, e.g.
    /// <c>/@name</c> on TikTok. Used to warn before expanding something unbounded.
    /// </summary>
    public string[] CollectionSegments { get; init; } = [];

    /// <summary>First path segments that always identify exactly one item.</summary>
    public string[] ItemSegments { get; init; } = [];

    /// <summary>
    /// True when this site's audio is encrypted, so a link is a catalogue reference rather than
    /// something to fetch.
    /// <para>
    /// These links are still worth accepting -- they carry the identity of a release, which is
    /// what makes it findable somewhere that serves audio openly, and what makes the result
    /// taggable properly. They are simply routed somewhere other than the extractor.
    /// </para>
    /// </summary>
    public bool IsEncryptedCatalogue { get; init; }

    /// <summary>
    /// Query parameters this site uses to record who shared the link.
    /// <para>
    /// Kept per-site rather than in one global list because the same short names mean different
    /// things elsewhere: <c>t</c> is a share token on X and a start timestamp on half the web,
    /// so stripping it everywhere would silently discard the part of the link the user cared about.
    /// </para>
    /// </summary>
    public string[] TrackingParameters { get; init; } = [];
}

/// <summary>
/// The sites the interface describes by name.
/// <para>
/// Absence from this list is not a refusal. An unrecognised host is still handed to the
/// extractor as <see cref="MediaProvider.Generic"/>: yt-dlp carries over 1700 extractors and
/// hard-coding which ones exist here would mean the app going stale between its releases and
/// the tool's. This catalogue only decides how confidently the app can talk about a link
/// before it has asked.
/// </para>
/// </summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDescriptor> All { get; } =
    [
        new()
        {
            Provider = MediaProvider.YouTubeMusic,
            DisplayName = "YouTube Music",
            Hosts = ["music.youtube.com"],
            IsAudioFirst = true,
            CollectionSegments = ["playlist", "browse", "channel"],
            ItemSegments = ["watch"],
        },
        new()
        {
            Provider = MediaProvider.YouTube,
            DisplayName = "YouTube",
            Hosts = ["youtube.com", "youtu.be", "youtube-nocookie.com"],
            CollectionSegments = ["playlist", "channel", "c", "user", "@"],
            ItemSegments = ["watch", "shorts", "embed", "live", "v"],
        },
        new()
        {
            Provider = MediaProvider.Instagram,
            DisplayName = "Instagram",
            Hosts = ["instagram.com", "instagr.am", "ddinstagram.com"],
            // Anonymous access is mostly closed; the advice is worth giving before the attempt.
            SignIn = SignInExpectation.Usually,
            CollectionSegments = ["stories"],
            ItemSegments = ["p", "reel", "reels", "tv"],
            TrackingParameters = ["igsh", "igshid", "img_index"],
        },
        new()
        {
            Provider = MediaProvider.TikTok,
            DisplayName = "TikTok",
            Hosts = ["tiktok.com", "tiktokv.com", "vm.tiktok.com", "vt.tiktok.com"],
            CollectionSegments = ["tag", "music", "collection"],
            ItemSegments = ["video", "photo", "share", "t", "embed"],
            TrackingParameters =
            [
                "_r", "_t", "tt_from", "share_app_id", "share_link_id", "share_item_id",
                "checksum", "is_from_webapp", "sender_device", "web_id",
            ],
        },
        new()
        {
            Provider = MediaProvider.Twitter,
            DisplayName = "X",
            Hosts = ["x.com", "twitter.com", "t.co", "fxtwitter.com", "vxtwitter.com"],
            SignIn = SignInExpectation.Sometimes,
            ItemSegments = ["status", "statuses", "i"],
            TrackingParameters = ["s", "t", "cxt", "twclid", "ref_src", "ref_url"],
        },
        new()
        {
            Provider = MediaProvider.Facebook,
            DisplayName = "Facebook",
            Hosts = ["facebook.com", "fb.com", "fb.watch", "m.facebook.com"],
            SignIn = SignInExpectation.Sometimes,
            CollectionSegments = ["groups"],
            ItemSegments = ["reel", "video", "videos", "watch", "share", "posts"],
            TrackingParameters = ["mibextid", "rdid", "share_url", "idorvanity"],
        },
        new()
        {
            Provider = MediaProvider.Reddit,
            DisplayName = "Reddit",
            Hosts = ["reddit.com", "redd.it", "old.reddit.com"],
            CollectionSegments = ["r", "user"],
            ItemSegments = ["comments"],
        },
        new()
        {
            Provider = MediaProvider.Vimeo,
            DisplayName = "Vimeo",
            Hosts = ["vimeo.com", "player.vimeo.com"],
            // Measured: the web client refuses anonymous requests and names cookies in the error.
            SignIn = SignInExpectation.Usually,
            CollectionSegments = ["album", "channels", "groups", "showcase"],
        },
        new()
        {
            Provider = MediaProvider.Twitch,
            DisplayName = "Twitch",
            Hosts = ["twitch.tv", "clips.twitch.tv"],
            CollectionSegments = ["videos", "collections"],
            ItemSegments = ["videos", "clip"],
        },
        new()
        {
            Provider = MediaProvider.SoundCloud,
            DisplayName = "SoundCloud",
            Hosts = ["soundcloud.com", "snd.sc", "on.soundcloud.com"],
            IsAudioFirst = true,
            CollectionSegments = ["sets", "likes", "tracks", "albums"],
        },
        new()
        {
            Provider = MediaProvider.Bandcamp,
            DisplayName = "Bandcamp",
            Hosts = ["bandcamp.com"],
            IsAudioFirst = true,
            CollectionSegments = ["album", "music"],
            ItemSegments = ["track"],
        },
        new()
        {
            Provider = MediaProvider.Bluesky,
            DisplayName = "Bluesky",
            Hosts = ["bsky.app", "bsky.social"],
            ItemSegments = ["post"],
            CollectionSegments = ["profile"],
        },
        new()
        {
            Provider = MediaProvider.Pinterest,
            DisplayName = "Pinterest",
            Hosts = ["pinterest.com", "pin.it", "pinterest.co.uk"],
            ItemSegments = ["pin"],
        },
        new()
        {
            Provider = MediaProvider.Dailymotion,
            DisplayName = "Dailymotion",
            Hosts = ["dailymotion.com", "dai.ly"],
            ItemSegments = ["video"],
            CollectionSegments = ["playlist", "user"],
        },
        new()
        {
            Provider = MediaProvider.Snapchat,
            DisplayName = "Snapchat",
            Hosts = ["snapchat.com"],
            ItemSegments = ["spotlight"],
        },
        new()
        {
            Provider = MediaProvider.LinkedIn,
            DisplayName = "LinkedIn",
            Hosts = ["linkedin.com", "lnkd.in"],
            SignIn = SignInExpectation.Sometimes,
            ItemSegments = ["posts", "feed"],
        },
        new()
        {
            Provider = MediaProvider.Kick,
            DisplayName = "Kick",
            Hosts = ["kick.com"],
            ItemSegments = ["video", "clip"],
        },
        new()
        {
            Provider = MediaProvider.Rumble,
            DisplayName = "Rumble",
            Hosts = ["rumble.com"],
            CollectionSegments = ["c", "user"],
        },
        new()
        {
            Provider = MediaProvider.Tumblr,
            DisplayName = "Tumblr",
            Hosts = ["tumblr.com"],
            ItemSegments = ["post"],
        },
        new()
        {
            Provider = MediaProvider.Vk,
            DisplayName = "VK",
            Hosts = ["vk.com", "vkvideo.ru"],
            ItemSegments = ["video", "clip"],
        },
        new()
        {
            Provider = MediaProvider.Bilibili,
            DisplayName = "Bilibili",
            Hosts = ["bilibili.com", "b23.tv"],
            ItemSegments = ["video", "bangumi"],
        },
        new()
        {
            Provider = MediaProvider.Streamable,
            DisplayName = "Streamable",
            Hosts = ["streamable.com"],
        },
        new()
        {
            Provider = MediaProvider.Odysee,
            DisplayName = "Odysee",
            Hosts = ["odysee.com", "lbry.tv"],
        },

        // --- Encrypted catalogues -----------------------------------------------------------
        // Audio from these is DRM-protected and is not downloaded. The link is read for what the
        // release is, and the recording is then looked for where it can be fetched openly.
        new()
        {
            Provider = MediaProvider.Spotify,
            DisplayName = "Spotify",
            Hosts = ["spotify.com", "spotify.link"],
            IsAudioFirst = true,
            IsEncryptedCatalogue = true,
            ItemSegments = ["track", "episode"],
            CollectionSegments = ["album", "playlist", "artist", "show"],
            TrackingParameters = ["si", "nd", "context", "utm_source", "go"],
        },
        new()
        {
            Provider = MediaProvider.AppleMusic,
            DisplayName = "Apple Music",
            Hosts = ["music.apple.com", "itunes.apple.com"],
            IsAudioFirst = true,
            IsEncryptedCatalogue = true,
            CollectionSegments = ["album", "playlist", "artist"],
            TrackingParameters = ["l", "app", "at", "ct", "uo", "ls"],
        },
        new()
        {
            Provider = MediaProvider.Deezer,
            DisplayName = "Deezer",
            Hosts = ["deezer.com", "dzr.page.link", "deezer.page.link"],
            IsAudioFirst = true,
            IsEncryptedCatalogue = true,
            ItemSegments = ["track", "episode"],
            CollectionSegments = ["album", "playlist", "artist", "podcast"],
            TrackingParameters = ["utm_source", "utm_campaign", "deferredFl", "host"],
        },
        new()
        {
            Provider = MediaProvider.Tidal,
            DisplayName = "TIDAL",
            Hosts = ["tidal.com"],
            IsAudioFirst = true,
            IsEncryptedCatalogue = true,
            ItemSegments = ["track", "video"],
            CollectionSegments = ["album", "playlist", "artist", "mix"],
            TrackingParameters = ["u", "play"],
        },
        new()
        {
            Provider = MediaProvider.AmazonMusic,
            DisplayName = "Amazon Music",
            // Only the music subdomains. Bare amazon.com is a shop, and yt-dlp has working
            // extractors for its product and review videos that this must not shadow.
            Hosts = ["music.amazon.com", "music.amazon.co.uk", "music.amazon.de",
                     "music.amazon.co.jp", "music.amazon.com.br", "music.amazon.fr",
                     "music.amazon.es", "music.amazon.it", "music.amazon.in"],
            IsAudioFirst = true,
            IsEncryptedCatalogue = true,
            ItemSegments = ["tracks"],
            CollectionSegments = ["albums", "playlists", "artists"],
            TrackingParameters = ["ref", "trackAsin", "do", "marketplaceId", "musicTerritory"],
        },
    ];

    private static readonly ProviderDescriptor GenericDescriptor = new()
    {
        Provider = MediaProvider.Generic,
        DisplayName = "",
        Hosts = [],
    };

    /// <summary>
    /// The descriptor for a host, or the generic one when the site is not described here.
    /// Never returns null: an unknown site is still a candidate, just an undescribed one.
    /// </summary>
    public static ProviderDescriptor ForHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return GenericDescriptor;

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();

        // Longest match wins so music.youtube.com is not swallowed by youtube.com.
        ProviderDescriptor? best = null;
        var bestLength = -1;

        foreach (var descriptor in All)
        {
            foreach (var candidate in descriptor.Hosts)
            {
                var matches = normalized.Equals(candidate, StringComparison.Ordinal)
                    || normalized.EndsWith("." + candidate, StringComparison.Ordinal);

                if (matches && candidate.Length > bestLength)
                {
                    best = descriptor;
                    bestLength = candidate.Length;
                }
            }
        }

        return best ?? GenericDescriptor;
    }

    public static ProviderDescriptor For(MediaProvider provider)
        => All.FirstOrDefault(d => d.Provider == provider) ?? GenericDescriptor;

    /// <summary>The site's name, or an empty string when the site is not one we describe.</summary>
    public static string DisplayName(MediaProvider provider) => For(provider).DisplayName;

    /// <summary>
    /// The music platform behind a provider, for the ones whose catalogue is read rather than
    /// downloaded. <see cref="Music.MusicPlatform.None"/> for everything else.
    /// </summary>
    public static Music.MusicPlatform ToMusicPlatform(MediaProvider provider) => provider switch
    {
        MediaProvider.Spotify => Music.MusicPlatform.Spotify,
        MediaProvider.AppleMusic => Music.MusicPlatform.AppleMusic,
        MediaProvider.Deezer => Music.MusicPlatform.Deezer,
        MediaProvider.Tidal => Music.MusicPlatform.Tidal,
        MediaProvider.AmazonMusic => Music.MusicPlatform.AmazonMusic,
        MediaProvider.YouTubeMusic => Music.MusicPlatform.YouTubeMusic,
        _ => Music.MusicPlatform.None,
    };
}

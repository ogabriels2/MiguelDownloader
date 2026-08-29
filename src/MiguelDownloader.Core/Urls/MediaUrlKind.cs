namespace MiguelDownloader.Core.Urls;

/// <summary>What a URL points at, decided purely from its shape (no network).</summary>
public enum MediaUrlKind
{
    Unknown = 0,
    /// <summary>A single watchable item (watch, youtu.be, a tweet, a reel, a post).</summary>
    Video,
    /// <summary>A short-form vertical item: YouTube Shorts, a Reel, a TikTok.</summary>
    Short,
    /// <summary>A regular playlist.</summary>
    Playlist,
    /// <summary>An album: a YouTube Music playlist (OLAK5uy_ / MPREb_), a Bandcamp album.</summary>
    Album,
    /// <summary>An auto-generated radio/mix. Effectively unbounded.</summary>
    Mix,
    /// <summary>A channel, optionally scoped to a tab (videos/shorts/streams/playlists).</summary>
    Channel,
    /// <summary>A person's page on a social network: a TikTok @handle, an Instagram profile.</summary>
    Profile,
    /// <summary>
    /// A post that may or may not carry media, which only the extractor can settle. Used where a
    /// site puts text and media behind the same URL shape, as X and Facebook do.
    /// </summary>
    Post,
    /// <summary>An ephemeral item: a story or a live broadcast in progress.</summary>
    Story,
    /// <summary>A search results page. Not a download target on its own.</summary>
    Search,
}

/// <summary>Which channel tab a channel URL was scoped to.</summary>
public enum ChannelTab
{
    Default = 0,
    Videos,
    Shorts,
    Streams,
    Playlists,
    Releases,
    Podcasts,
}

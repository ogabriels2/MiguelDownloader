namespace MiguelDownloader.Core.Urls;

/// <summary>
/// The result of parsing a user-supplied URL. Everything here comes from the URL
/// text alone; nothing is contacted. <see cref="CanonicalUrl"/> is what we hand to yt-dlp.
/// </summary>
/// <remarks>
/// The fields below are what a URL can say about itself before anyone asks the site. Sites differ
/// in how much that is: a YouTube watch URL carries a video id, a playlist and an offset, while an
/// X status URL carries an id and nothing else. Absent detail is left null rather than guessed —
/// the extractor's answer is the authority, and this type exists only to choose the right question
/// and to warn before expanding something unbounded.
/// </remarks>
public sealed record MediaUrlInfo
{
    /// <summary>The site this came from, or <see cref="MediaProvider.Generic"/> when undescribed.</summary>
    public MediaProvider Provider { get; init; } = MediaProvider.Generic;

    public required MediaUrlKind Kind { get; init; }

    /// <summary>The URL we pass to the extractor, cleaned of tracking noise.</summary>
    public required string CanonicalUrl { get; init; }

    /// <summary>The URL exactly as the user supplied it.</summary>
    public required string OriginalUrl { get; init; }

    public string? VideoId { get; init; }
    public string? PlaylistId { get; init; }
    public string? ChannelId { get; init; }

    /// <summary>The @handle form, without the leading '@'.</summary>
    public string? Handle { get; init; }

    /// <summary>Legacy /c/Name or /user/Name segment.</summary>
    public string? LegacyChannelName { get; init; }

    public ChannelTab Tab { get; init; } = ChannelTab.Default;

    /// <summary>True when the URL came from music.youtube.com.</summary>
    public bool IsMusicDomain { get; init; }

    /// <summary>A video URL that also carried a &amp;list= context.</summary>
    public bool HasPlaylistContext => PlaylistId is not null && VideoId is not null;

    /// <summary>1-based index within the playlist, when the URL carried one.</summary>
    public int? PlaylistIndex { get; init; }

    /// <summary>Start offset from ?t= / #t=, when present.</summary>
    public TimeSpan? StartAt { get; init; }

    public string? SearchQuery { get; init; }

    /// <summary>
    /// True when this resolves to something we can actually fetch.
    /// <para>
    /// <see cref="MediaUrlKind.Unknown"/> counts on every site but YouTube. YouTube's URL shapes
    /// are finite and known, so an unrecognised one is genuinely wrong; elsewhere the shapes
    /// change often enough that refusing an unfamiliar path would mean rejecting links that work.
    /// The extractor decides those, and says so plainly when it cannot.
    /// </para>
    /// </summary>
    public bool IsDownloadable => Kind switch
    {
        MediaUrlKind.Search => false,
        MediaUrlKind.Unknown => Provider is not (MediaProvider.YouTube or MediaProvider.YouTubeMusic),
        _ => true,
    };

    /// <summary>True when this expands to many items and deserves a confirmation step.</summary>
    public bool IsBulk => Kind is MediaUrlKind.Playlist or MediaUrlKind.Album
        or MediaUrlKind.Mix or MediaUrlKind.Channel or MediaUrlKind.Profile;

    /// <summary>The site's own name, or an empty string when the site is not one we describe.</summary>
    public string ProviderName => ProviderCatalog.DisplayName(Provider);

    /// <summary>
    /// True when the site usually wants a session. Drives advice, never a refusal: plenty of
    /// public items on these sites work anonymously, and the only way to know is to ask.
    /// </summary>
    public bool SignInLikely => ProviderCatalog.For(Provider).SignIn == SignInExpectation.Usually;

    /// <summary>True for sites that serve audio, where opening on the music flow is correct.</summary>
    public bool IsAudioFirst => IsMusicDomain || ProviderCatalog.For(Provider).IsAudioFirst;

    /// <summary>
    /// True when this link points at a catalogue whose audio is encrypted, so it is resolved to a
    /// list of recordings and each one looked for elsewhere, rather than handed to the extractor.
    /// </summary>
    public bool IsEncryptedCatalogue => ProviderCatalog.For(Provider).IsEncryptedCatalogue;

    /// <summary>The music platform behind this link, when it is one whose catalogue we read.</summary>
    public Music.MusicPlatform MusicPlatform => ProviderCatalog.ToMusicPlatform(Provider);
}

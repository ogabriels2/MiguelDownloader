namespace MiguelDownloader.Core.Models;

/// <summary>What the analysed content actually is, after the extractor has spoken.</summary>
public enum MediaKind
{
    Unknown = 0,
    Video,
    Short,
    /// <summary>A music track: from YouTube Music, or a video the source tags as music.</summary>
    Music,
    /// <summary>A music video: visual content whose primary value is the song.</summary>
    MusicVideo,
    LiveStream,
    Playlist,
    Album,
    Channel,
}

/// <summary>Whether the app should present the video flow or the music flow.</summary>
public enum ContentProfile
{
    Video = 0,
    Music,
}

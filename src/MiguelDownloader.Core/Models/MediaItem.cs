namespace MiguelDownloader.Core.Models;

/// <summary>
/// One analysed, downloadable item: a video, a short, or a music track.
/// Collections hold a list of these.
/// </summary>
public sealed record MediaItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string WebpageUrl { get; init; }

    public string? Uploader { get; init; }
    public string? ChannelName { get; init; }
    public string? ChannelId { get; init; }
    public string? ChannelUrl { get; init; }

    public TimeSpan? Duration { get; init; }
    public string? Description { get; init; }
    public DateOnly? UploadDate { get; init; }
    public long? ViewCount { get; init; }
    public string? License { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Video;

    /// <summary>Live/upcoming state as reported ("not_live", "is_live", "is_upcoming", "was_live").</summary>
    public string? LiveStatus { get; init; }

    /// <summary>"public", "private", "unlisted", "needs_auth", "subscriber_only"...</summary>
    public string? Availability { get; init; }

    public int AgeLimit { get; init; }

    /// <summary>Music tags the extractor supplied. Never fabricated.</summary>
    public MusicMetadata? Music { get; init; }

    public IReadOnlyList<MediaFormat> Formats { get; init; } = [];
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];
    public IReadOnlyList<ThumbnailInfo> Thumbnails { get; init; } = [];

    /// <summary>1-based position when this item came from a playlist or album.</summary>
    public int? PlaylistIndex { get; init; }

    /// <summary>True when only a stub was fetched (flat playlist listing) and formats are not loaded yet.</summary>
    public bool IsStub { get; init; }

    public bool IsLive => string.Equals(LiveStatus, "is_live", StringComparison.OrdinalIgnoreCase);
    public bool IsUpcoming => string.Equals(LiveStatus, "is_upcoming", StringComparison.OrdinalIgnoreCase);

    /// <summary>Any stream flagged DRM. We surface this and refuse rather than work around it.</summary>
    public bool HasDrmFormats => Formats.Any(f => f.HasDrm);

    /// <summary>Formats worth showing: no storyboards, no DRM.</summary>
    public IEnumerable<MediaFormat> SelectableFormats =>
        Formats.Where(f => !f.IsStoryboard && !f.HasDrm);

    public ThumbnailInfo? BestThumbnail =>
        Thumbnails.Where(t => t.Pixels > 0).OrderByDescending(t => t.Pixels).FirstOrDefault()
        ?? Thumbnails.LastOrDefault();

    public string DisplayAuthor => Music?.Artist ?? ChannelName ?? Uploader ?? string.Empty;
}

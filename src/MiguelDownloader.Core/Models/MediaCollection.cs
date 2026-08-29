namespace MiguelDownloader.Core.Models;

/// <summary>A playlist, album or channel: an ordered set of items plus its own identity.</summary>
public sealed record MediaCollection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string WebpageUrl { get; init; }

    public MediaKind Kind { get; init; } = MediaKind.Playlist;

    public string? Uploader { get; init; }
    public string? ChannelName { get; init; }
    public string? Description { get; init; }

    /// <summary>Album artist for album collections, when the source names one.</summary>
    public string? AlbumArtist { get; init; }
    public int? Year { get; init; }

    public IReadOnlyList<ThumbnailInfo> Thumbnails { get; init; } = [];

    /// <summary>Items in source order. May be stubs when the listing was fetched flat.</summary>
    public IReadOnlyList<MediaItem> Items { get; init; } = [];

    /// <summary>
    /// Total the source claims, which can exceed <see cref="Items"/> when the listing was
    /// truncated (a channel capped at N, or a mix that never really ends).
    /// </summary>
    public int? DeclaredCount { get; init; }

    /// <summary>True when we stopped short of the full listing.</summary>
    public bool IsTruncated => DeclaredCount is { } d && d > Items.Count;

    public int Count => Items.Count;

    public ThumbnailInfo? BestThumbnail =>
        Thumbnails.Where(t => t.Pixels > 0).OrderByDescending(t => t.Pixels).FirstOrDefault()
        ?? Thumbnails.LastOrDefault()
        ?? Items.Select(i => i.BestThumbnail).FirstOrDefault(t => t is not null);

    /// <summary>Total runtime of the items we know durations for.</summary>
    public TimeSpan? TotalDuration
    {
        get
        {
            var known = Items.Where(i => i.Duration is not null).ToList();
            return known.Count == 0 ? null : TimeSpan.FromSeconds(known.Sum(i => i.Duration!.Value.TotalSeconds));
        }
    }
}

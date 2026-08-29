using MiguelDownloader.Core.Urls;

namespace MiguelDownloader.Core.Models;

/// <summary>
/// What a URL turned out to be. Exactly one of <see cref="Item"/> or <see cref="Collection"/>
/// is set; <see cref="IsCollection"/> says which.
/// </summary>
public sealed record AnalysisResult
{
    public required MediaUrlInfo Source { get; init; }

    /// <summary>Set when the URL resolved to a single downloadable item.</summary>
    public MediaItem? Item { get; init; }

    /// <summary>Set when the URL resolved to a playlist, album or channel.</summary>
    public MediaCollection? Collection { get; init; }

    /// <summary>Which flow the UI should present by default.</summary>
    public ContentProfile Profile { get; init; } = ContentProfile.Video;

    /// <summary>
    /// Non-fatal notes worth showing: a partially unavailable playlist, a missing JS runtime,
    /// a truncated channel listing. Never used for anything that should have failed the analysis.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsCollection => Collection is not null;

    public string Title => Collection?.Title ?? Item?.Title ?? string.Empty;

    public MediaKind Kind => Collection?.Kind ?? Item?.Kind ?? MediaKind.Unknown;

    public ThumbnailInfo? Thumbnail => Collection?.BestThumbnail ?? Item?.BestThumbnail;

    /// <summary>Every downloadable item, whether this was one video or a whole album.</summary>
    public IReadOnlyList<MediaItem> AllItems =>
        Collection?.Items ?? (Item is null ? [] : [Item]);
}

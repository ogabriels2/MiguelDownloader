namespace MiguelDownloader.Core.Models;

/// <summary>One available cover/thumbnail image.</summary>
public sealed record ThumbnailInfo
{
    public required string Url { get; init; }
    public string? Id { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }

    /// <summary>Extractor-supplied ordering hint; higher is better.</summary>
    public int? Preference { get; init; }

    public long Pixels => (long)(Width ?? 0) * (Height ?? 0);

    public string DisplaySize => Width is > 0 && Height is > 0 ? $"{Width}x{Height}" : "?";
}

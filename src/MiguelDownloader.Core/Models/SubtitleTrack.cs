namespace MiguelDownloader.Core.Models;

/// <summary>Where a subtitle track came from. Drives both labelling and default selection.</summary>
public enum SubtitleOrigin
{
    /// <summary>Uploaded by the creator, or an official track.</summary>
    Manual = 0,
    /// <summary>Speech recognition. Useful, but not authored.</summary>
    Automatic,
    /// <summary>Machine translation of another track.</summary>
    Translated,
}

/// <summary>One subtitle track offered by the source.</summary>
public sealed record SubtitleTrack
{
    public required string LanguageCode { get; init; }

    /// <summary>Human name as reported, e.g. "Portuguese (Brazil)".</summary>
    public string? LanguageName { get; init; }

    public required SubtitleOrigin Origin { get; init; }

    /// <summary>Formats the source can deliver this track in (vtt, srv3, ttml...).</summary>
    public IReadOnlyList<string> AvailableFormats { get; init; } = [];

    public string DisplayName => LanguageName is { Length: > 0 } ? $"{LanguageName} ({LanguageCode})" : LanguageCode;
}

/// <summary>Subtitle file formats we can write.</summary>
public enum SubtitleFormat
{
    /// <summary>Keep whatever the source provides, no conversion.</summary>
    Original = 0,
    Srt,
    Vtt,
    Ass,
}

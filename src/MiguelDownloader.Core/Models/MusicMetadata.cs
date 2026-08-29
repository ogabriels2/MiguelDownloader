namespace MiguelDownloader.Core.Models;

/// <summary>
/// Music tags for one track. Every property is nullable on purpose: a field is written to the
/// file only when the source actually supplied it. We never synthesise a genre, a year or a
/// track number that has no origin in the extracted data.
/// </summary>
public sealed record MusicMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public int? Year { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Genre { get; init; }
    public string? Composer { get; init; }
    public string? Copyright { get; init; }
    public string? Comment { get; init; }
    public string? SourceUrl { get; init; }

    /// <summary>The upload/track id, stored so a file can be traced back to its source.</summary>
    public string? SourceId { get; init; }

    // --- Catalogue identifiers -------------------------------------------------------------
    // These come from a music platform's public catalogue rather than from the audio source, and
    // they are what make a library actually manageable: the ISRC identifies a recording across
    // every service that carries it, so a file tagged with one can be matched, de-duplicated and
    // looked up later without relying on the spelling of a title.

    /// <summary>International Standard Recording Code: identifies this recording globally.</summary>
    public string? Isrc { get; init; }

    /// <summary>The release's barcode (UPC/EAN), identifying the album rather than the track.</summary>
    public string? Barcode { get; init; }

    /// <summary>The record label that issued the release.</summary>
    public string? Label { get; init; }

    /// <summary>Beats per minute, when the catalogue reports a measured value.</summary>
    public double? BeatsPerMinute { get; init; }

    /// <summary>
    /// Loudness offset in dB, written as the ReplayGain track gain so players can level a mixed
    /// library without re-encoding anything.
    /// </summary>
    public double? ReplayGainTrackGain { get; init; }

    /// <summary>MusicBrainz recording id, the canonical handle for this performance.</summary>
    public string? MusicBrainzRecordingId { get; init; }

    /// <summary>MusicBrainz release id, the canonical handle for this album edition.</summary>
    public string? MusicBrainzReleaseId { get; init; }

    public string? MusicBrainzArtistId { get; init; }

    /// <summary>True when we have enough to name a file sensibly in album layout.</summary>
    public bool HasAlbumContext => !string.IsNullOrWhiteSpace(Album);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist) &&
        string.IsNullOrWhiteSpace(Album) && TrackNumber is null;
}

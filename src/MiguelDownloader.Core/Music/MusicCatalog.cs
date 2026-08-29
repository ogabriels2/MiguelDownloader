using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Music;

/// <summary>What a music-platform link turned out to describe.</summary>
public enum CatalogKind
{
    Unknown = 0,
    Track,
    Album,
    Playlist,
    Artist,
}

/// <summary>
/// One track as the platform's catalogue describes it.
/// <para>
/// This is metadata about a recording, not a downloadable stream. The audio on Spotify, Apple
/// Music, Tidal, Amazon Music and Deezer is encrypted, and this application does not break
/// encryption, so what a catalogue entry provides is the identity of the recording: enough to
/// find it somewhere that serves audio openly, and enough to tag the result properly once found.
/// </para>
/// </summary>
public sealed record CatalogTrack
{
    public required string Title { get; init; }
    public required string Artist { get; init; }

    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public int? DiscNumber { get; init; }
    public int? DiscTotal { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Genre { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Label { get; init; }
    public string? Copyright { get; init; }
    public string? Composer { get; init; }

    /// <summary>
    /// International Standard Recording Code.
    /// <para>
    /// The single most useful field here. It identifies the exact recording independently of how
    /// anyone spelled the title, so it is what lets a match be verified rather than guessed, and
    /// what lets the same track be recognised again later.
    /// </para>
    /// </summary>
    public string? Isrc { get; init; }

    /// <summary>The release barcode (UPC/EAN).</summary>
    public string? Barcode { get; init; }

    public double? BeatsPerMinute { get; init; }
    public double? ReplayGainTrackGain { get; init; }

    public string? MusicBrainzRecordingId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzArtistId { get; init; }

    /// <summary>Highest-resolution cover art the catalogue offers.</summary>
    public string? CoverArtUrl { get; init; }

    /// <summary>The track's page on the platform it was read from.</summary>
    public string? PlatformUrl { get; init; }

    /// <summary>Which platform described it.</summary>
    public MusicPlatform Platform { get; init; }

    /// <summary>What to search for when looking this recording up elsewhere.</summary>
    public string SearchQuery => $"{Artist} - {Title}".Trim(' ', '-');

    public MusicMetadata ToMetadata() => new()
    {
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist ?? Artist,
        Album = Album,
        TrackNumber = TrackNumber,
        TrackTotal = TrackTotal,
        DiscNumber = DiscNumber,
        DiscTotal = DiscTotal,
        Year = ParseYear(ReleaseDate),
        ReleaseDate = ReleaseDate,
        Genre = Genre,
        Composer = Composer,
        Copyright = Copyright,
        Label = Label,
        Isrc = Isrc,
        Barcode = Barcode,
        BeatsPerMinute = BeatsPerMinute is > 0 ? BeatsPerMinute : null,
        ReplayGainTrackGain = ReplayGainTrackGain,
        MusicBrainzRecordingId = MusicBrainzRecordingId,
        MusicBrainzReleaseId = MusicBrainzReleaseId,
        MusicBrainzArtistId = MusicBrainzArtistId,
        SourceUrl = PlatformUrl,
    };

    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate)) return null;
        return int.TryParse(releaseDate.AsSpan(0, Math.Min(4, releaseDate.Length)), out var year)
            && year is > 1000 and < 3000
            ? year
            : null;
    }
}

/// <summary>
/// A resolved music-platform link: what it is, and every track it contains.
/// </summary>
public sealed record MusicCatalog
{
    public required CatalogKind Kind { get; init; }
    public required string Title { get; init; }
    public required MusicPlatform Platform { get; init; }

    public string? Artist { get; init; }
    public string? CoverArtUrl { get; init; }
    public string? ReleaseDate { get; init; }
    public string? Label { get; init; }
    public string? Barcode { get; init; }
    public string? PlatformUrl { get; init; }

    public IReadOnlyList<CatalogTrack> Tracks { get; init; } = [];

    /// <summary>
    /// How many tracks the platform said there are. When this exceeds <see cref="Tracks"/>, the
    /// listing was capped or partially resolved, and the interface must say so rather than
    /// presenting a short list as complete.
    /// </summary>
    public int? DeclaredCount { get; init; }

    public bool IsComplete => DeclaredCount is null || DeclaredCount <= Tracks.Count;

    /// <summary>
    /// True when the catalogue was found by looking the title up on another platform rather than
    /// read from the one in the link.
    /// <para>
    /// Worth surfacing: a bridged result is the right album almost always and the wrong one
    /// occasionally, and the user is the one who can tell at a glance.
    /// </para>
    /// </summary>
    public bool WasBridged { get; init; }

    /// <summary>The platform the tracks were actually read from, when bridged.</summary>
    public MusicPlatform? BridgedVia { get; init; }
}

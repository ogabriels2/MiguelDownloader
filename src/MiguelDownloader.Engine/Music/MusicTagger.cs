using MiguelDownloader.Core.Models;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Music;

/// <summary>
/// Writes music tags and cover art into finished audio files.
/// <para>
/// The downloader already embeds what it knows, but it works from the upload's own fields. When
/// the app has better information, most importantly the position of a track within an album it
/// listed, this writes the complete and correct set afterwards.
/// </para>
/// <para>
/// Only fields with a real source are written. A missing genre stays missing rather than being
/// guessed from the channel, because a wrong tag propagates into the user's library and is far
/// more annoying to undo than an empty one.
/// </para>
/// </summary>
public sealed class MusicTagger(ILogger<MusicTagger> logger)
{
    private readonly ILogger<MusicTagger> _logger = logger;

    /// <summary>Image types TagLib can embed reliably.</summary>
    private static readonly string[] SupportedCoverExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>
    /// Applies tags to a file.
    /// </summary>
    /// <param name="filePath">The audio file to tag.</param>
    /// <param name="metadata">Tags to write. Null fields are left untouched.</param>
    /// <param name="coverImagePath">Optional cover image to embed.</param>
    /// <returns>True when the file was written; false when tagging was not possible.</returns>
    public bool Apply(string filePath, MusicMetadata metadata, string? coverImagePath = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Cannot tag a file that does not exist: {Path}", filePath);
            return false;
        }

        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag = file.Tag;

            if (metadata.Title is { Length: > 0 } title) tag.Title = title;

            if (metadata.Artist is { Length: > 0 } artist)
                tag.Performers = [artist];

            if (metadata.AlbumArtist is { Length: > 0 } albumArtist)
                tag.AlbumArtists = [albumArtist];

            if (metadata.Album is { Length: > 0 } album) tag.Album = album;

            if (metadata.TrackNumber is > 0) tag.Track = (uint)metadata.TrackNumber.Value;
            if (metadata.TrackTotal is > 0) tag.TrackCount = (uint)metadata.TrackTotal.Value;
            if (metadata.DiscNumber is > 0) tag.Disc = (uint)metadata.DiscNumber.Value;
            if (metadata.DiscTotal is > 0) tag.DiscCount = (uint)metadata.DiscTotal.Value;

            if (metadata.Year is > 0 and < 3000) tag.Year = (uint)metadata.Year.Value;

            if (metadata.Genre is { Length: > 0 } genre) tag.Genres = [genre];
            if (metadata.Composer is { Length: > 0 } composer) tag.Composers = [composer];
            if (metadata.Copyright is { Length: > 0 } copyright) tag.Copyright = copyright;

            // The source URL goes in the comment so a file can always be traced back, which also
            // makes re-downloading at a different quality straightforward later.
            var comment = metadata.Comment ?? metadata.SourceUrl;
            if (comment is { Length: > 0 }) tag.Comment = comment;

            WriteCatalogueIdentifiers(file, tag, metadata);

            EmbedCover(file, coverImagePath);

            file.Save();
            _logger.LogDebug("Wrote tags to {File}", Path.GetFileName(filePath));
            return true;
        }
        catch (TagLib.UnsupportedFormatException)
        {
            // Opus in a WebM container is one of these. The downloader has already written what it
            // could, so this is a limitation rather than a failure of the download.
            _logger.LogInformation("Tag format not supported for {File}; keeping the tags the downloader wrote",
                Path.GetFileName(filePath));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or TagLib.CorruptFileException)
        {
            _logger.LogWarning(ex, "Could not write tags to {File}", Path.GetFileName(filePath));
            return false;
        }
    }

    private void EmbedCover(TagLib.File file, string? coverImagePath)
    {
        if (string.IsNullOrWhiteSpace(coverImagePath) || !File.Exists(coverImagePath)) return;

        var extension = Path.GetExtension(coverImagePath).ToLowerInvariant();
        if (!SupportedCoverExtensions.Contains(extension))
        {
            // WebP cover art is not portable across players, so it is skipped rather than written
            // into a file where half the world's software would show a blank square.
            _logger.LogDebug("Skipping cover art in unsupported format {Extension}", extension);
            return;
        }

        try
        {
            var picture = new TagLib.Picture(coverImagePath)
            {
                Type = TagLib.PictureType.FrontCover,
                Description = "Cover",
                MimeType = extension == ".png" ? "image/png" : "image/jpeg",
            };

            file.Tag.Pictures = [picture];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read the cover image");
        }
    }

    /// <summary>
    /// Builds the tags for one track of an album, combining what the item carries with the
    /// position and album identity the collection provides.
    /// </summary>
    /// <summary>
    /// Writes the identifiers and measurements a streaming catalogue supplies.
    /// <para>
    /// These are the fields that make a library manageable rather than merely playable: the ISRC
    /// identifies the recording across every service that carries it, the MusicBrainz ids tie the
    /// file to the open encyclopedia, and the ReplayGain figure lets a player level a mixed
    /// library without touching the audio.
    /// </para>
    /// <para>
    /// TagLib exposes some of these as first-class properties and the rest only per format, so
    /// they are written to whichever containers actually carry them: ID3v2 frames for MP3, the
    /// standard field names for Vorbis comments in FLAC, Opus and Ogg, and the iTunes-compatible
    /// atoms for MP4. A format that carries none of them is left alone rather than filled with
    /// something it cannot express.
    /// </para>
    /// </summary>
    private static void WriteCatalogueIdentifiers(
        TagLib.File file, TagLib.Tag tag, MusicMetadata metadata)
    {
        if (metadata.Isrc is { Length: > 0 } isrc) tag.ISRC = isrc;
        if (metadata.Label is { Length: > 0 } label) tag.Publisher = label;
        if (metadata.BeatsPerMinute is > 0 and < 1000 and { } bpm) tag.BeatsPerMinute = (uint)Math.Round(bpm);

        if (metadata.MusicBrainzRecordingId is { Length: > 0 } recording)
            tag.MusicBrainzTrackId = recording;
        if (metadata.MusicBrainzReleaseId is { Length: > 0 } release)
            tag.MusicBrainzReleaseId = release;
        if (metadata.MusicBrainzArtistId is { Length: > 0 } artistId)
            tag.MusicBrainzArtistId = artistId;

        // The barcode and the loudness offset have no first-class property, so they go in the
        // fields each container actually defines for them.
        var extras = new List<(string Name, string Value)>();
        if (metadata.Barcode is { Length: > 0 } barcode) extras.Add(("BARCODE", barcode));
        if (metadata.ReplayGainTrackGain is { } gain)
        {
            extras.Add(("REPLAYGAIN_TRACK_GAIN",
                gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " dB"));
        }
        if (extras.Count == 0) return;

        // FLAC, Opus and Ogg: Vorbis comments take arbitrary field names, which is exactly what
        // these two are conventionally stored as.
        if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            foreach (var (name, value) in extras) xiph.SetField(name, value);

            // In an Ogg stream the tag handed out is a grouped view over the comments inside it,
            // and it forwards some properties to them but not others: the beats per minute and
            // the MusicBrainz ids arrive, while the ISRC and the publisher are dropped silently.
            // FLAC keeps a Xiph comment directly and is unaffected, which is why this only shows
            // in Opus -- exactly the container a track from YouTube arrives in.
            if (metadata.Isrc is { Length: > 0 } xiphIsrc) xiph.SetField("ISRC", xiphIsrc);
            if (metadata.Label is { Length: > 0 } xiphLabel)
            {
                // ORGANIZATION is the field the Vorbis comment specification names for this;
                // LABEL is what most players actually read.
                xiph.SetField("ORGANIZATION", xiphLabel);
                xiph.SetField("LABEL", xiphLabel);
            }
        }

        // MP3: user-defined text frames, the ID3v2 equivalent.
        if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var (name, value) in extras)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, name, create: true);
                frame.Text = [value];
            }
        }

        // MP4/M4A: freeform atoms under the iTunes namespace, which is where players look.
        if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            foreach (var (name, value) in extras)
                apple.SetDashBox("com.apple.iTunes", name, value);
        }
    }

    public static MusicMetadata ForAlbumTrack(
        MediaItem item, MediaCollection album, int trackNumber, int trackTotal)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(album);

        var source = item.Music;

        return new MusicMetadata
        {
            // The track title from the music tags is cleaner than the video title, which often
            // carries decoration like "(Official Video)".
            Title = source?.Title ?? item.Title,
            Artist = source?.Artist ?? album.AlbumArtist,
            AlbumArtist = album.AlbumArtist ?? source?.AlbumArtist ?? source?.Artist,
            Album = source?.Album ?? album.Title,
            // Position within the listing is authoritative for an album; the upload's own track
            // number refers to its original release and is often absent or wrong here.
            TrackNumber = trackNumber,
            TrackTotal = trackTotal,
            DiscNumber = source?.DiscNumber,
            Year = source?.Year ?? album.Year,
            ReleaseDate = source?.ReleaseDate,
            Genre = source?.Genre,
            Composer = source?.Composer,
            Copyright = source?.Copyright,
            SourceUrl = item.WebpageUrl,
            SourceId = item.Id,
        };
    }

    /// <summary>Builds the tags for a standalone track that is not part of an album.</summary>
    public static MusicMetadata ForSingle(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var source = item.Music;

        return new MusicMetadata
        {
            Title = source?.Title ?? item.Title,
            // Falling back to the channel name is reasonable for a single: on YouTube Music the
            // channel is the artist. It is still only used when no explicit artist tag exists.
            Artist = source?.Artist ?? item.ChannelName,
            AlbumArtist = source?.AlbumArtist ?? source?.Artist,
            Album = source?.Album,
            TrackNumber = source?.TrackNumber,
            Year = source?.Year ?? item.UploadDate?.Year,
            ReleaseDate = source?.ReleaseDate,
            Genre = source?.Genre,
            Composer = source?.Composer,
            Copyright = source?.Copyright,
            SourceUrl = item.WebpageUrl,
            SourceId = item.Id,
        };
    }
}

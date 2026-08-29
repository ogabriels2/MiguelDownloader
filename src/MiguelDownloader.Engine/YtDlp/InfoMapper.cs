using System.Text.Json;
using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Engine.YtDlp;

/// <summary>
/// Maps the extractor's JSON onto the domain models.
/// <para>
/// The mapping is deliberately conservative: a field is populated only when the source actually
/// supplied it. Nothing is inferred, defaulted to a plausible value, or rounded up, because every
/// number here eventually appears in the UI as a claim about the media.
/// </para>
/// </summary>
public static class InfoMapper
{
    /// <summary>True when the payload describes a playlist, album or channel rather than one video.</summary>
    public static bool IsCollection(JsonElement root)
    {
        var type = root.String("_type");
        return type is "playlist" or "multi_video";
    }

    public static MediaItem MapItem(JsonElement root, int? playlistIndex = null)
    {
        var id = root.String("id") ?? root.String("display_id") ?? string.Empty;
        var title = root.String("title") ?? root.String("fulltitle") ?? id;

        var webpageUrl = root.String("webpage_url")
            ?? root.String("original_url")
            ?? root.String("url")
            ?? (id.Length == 11 ? $"https://www.youtube.com/watch?v={id}" : string.Empty);

        var formats = root.Array("formats").Select(MapFormat).Where(f => f is not null).Select(f => f!).ToList();

        // A single-format response puts the stream at the top level instead of in "formats".
        if (formats.Count == 0 && root.String("format_id") is not null && MapFormat(root) is { } single)
            formats.Add(single);

        var music = MapMusic(root, webpageUrl, id);

        return new MediaItem
        {
            Id = id,
            Title = title,
            WebpageUrl = webpageUrl,
            Uploader = root.String("uploader"),
            ChannelName = root.String("channel") ?? root.String("uploader"),
            ChannelId = root.String("channel_id") ?? root.String("uploader_id"),
            ChannelUrl = root.String("channel_url") ?? root.String("uploader_url"),
            Duration = root.Duration("duration"),
            Description = root.String("description"),
            UploadDate = root.CompactDate("upload_date") ?? root.CompactDate("release_date"),
            ViewCount = root.Long("view_count"),
            License = root.String("license"),
            LiveStatus = root.String("live_status"),
            Availability = root.String("availability"),
            AgeLimit = root.Int("age_limit") ?? 0,
            Kind = ClassifyItem(root),
            Music = music.IsEmpty ? null : music,
            Formats = formats,
            Subtitles = MapSubtitles(root),
            Thumbnails = MapThumbnails(root),
            PlaylistIndex = playlistIndex ?? root.Int("playlist_index"),
            // A flat listing gives an id and a title but no formats; the caller must fetch details
            // before such an item can be downloaded.
            IsStub = formats.Count == 0 && root.String("_type") == "url",
        };
    }

    /// <summary>
    /// Decides whether this is music.
    /// <para>
    /// The strongest signal is the extractor's own <c>media_type</c>; after that, a populated
    /// <c>track</c>/<c>artist</c> pair, which YouTube only fills in for content it has matched to
    /// a release. Shorts are detected by their aspect ratio and duration only when the extractor
    /// has not already said so.
    /// </para>
    /// </summary>
    private static MediaKind ClassifyItem(JsonElement root)
    {
        var mediaType = root.String("media_type")?.ToLowerInvariant();

        if (mediaType is "short") return MediaKind.Short;
        if (mediaType is "music_video") return MediaKind.MusicVideo;

        var liveStatus = root.String("live_status");
        if (liveStatus is "is_live" or "is_upcoming") return MediaKind.LiveStream;

        var hasTrackTags = root.String("track") is not null &&
                           (root.String("artist") is not null || root.String("album") is not null);

        if (hasTrackTags)
        {
            // The Music extractor and the plain YouTube extractor both surface track tags; only
            // the former means the user is actually looking at a music page.
            var extractor = root.String("extractor_key") ?? root.String("extractor");
            var isMusicExtractor = extractor?.Contains("music", StringComparison.OrdinalIgnoreCase) ?? false;
            return isMusicExtractor ? MediaKind.Music : MediaKind.MusicVideo;
        }

        return MediaKind.Video;
    }

    private static MediaFormat? MapFormat(JsonElement element)
    {
        var formatId = element.String("format_id");
        if (formatId is null) return null;

        var rawVideo = element.String("vcodec");
        var rawAudio = element.String("acodec");

        var width = element.Int("width");
        var height = element.Int("height");
        var audioBitrate = element.Double("abr");

        // Not every site names its codecs. X leaves both fields out entirely while still reporting
        // a frame size and an audio bitrate, and reading that absence as "there is no such stream"
        // has two bad consequences: the stream gets discarded as carrying nothing, and where it
        // survives, a container check on a codec of None decides the audio cannot be copied and
        // re-encodes perfectly good AAC.
        //
        // A stream the source described by its measurements but not by its codec is one of unknown
        // codec, not an absent one.
        var videoCodec = CodecParser.ParseVideo(rawVideo);
        if (videoCodec == VideoCodec.None && rawVideo is null && (width is > 0 || height is > 0))
            videoCodec = VideoCodec.Unknown;

        var audioCodec = CodecParser.ParseAudio(rawAudio);
        if (audioCodec == AudioCodec.None && rawAudio is null && audioBitrate is > 0)
            audioCodec = AudioCodec.Unknown;

        return new MediaFormat
        {
            FormatId = formatId,
            Extension = element.String("ext") ?? "bin",
            RawVideoCodec = rawVideo,
            RawAudioCodec = rawAudio,
            VideoCodec = videoCodec,
            AudioCodec = audioCodec,
            Width = width,
            Height = height,
            Fps = element.Double("fps"),
            DynamicRange = element.String("dynamic_range"),
            TotalBitrate = element.Double("tbr"),
            VideoBitrate = element.Double("vbr"),
            AudioBitrate = audioBitrate,
            SampleRate = element.Int("asr"),
            AudioChannels = element.Int("audio_channels"),
            FileSize = element.Long("filesize"),
            FileSizeApprox = element.Long("filesize_approx"),
            Language = element.String("language"),
            FormatNote = element.String("format_note"),
            Protocol = element.String("protocol"),
            HasDrm = element.Bool("has_drm"),
        };
    }

    private static List<SubtitleTrack> MapSubtitles(JsonElement root)
    {
        var tracks = new List<SubtitleTrack>();

        foreach (var member in root.Members("subtitles"))
            AddTrack(member, SubtitleOrigin.Manual);

        foreach (var member in root.Members("automatic_captions"))
        {
            // Automatic captions include a translated copy for every language YouTube supports,
            // which is hundreds of entries. Only the ones actually generated from the audio are
            // useful; the rest are machine translations of those and are marked as such.
            var isTranslation = member.Name.Contains('-', StringComparison.Ordinal) &&
                                member.Value.EnumerateArray().Any(f =>
                                    (f.String("name") ?? string.Empty).Contains("from", StringComparison.OrdinalIgnoreCase));

            AddTrack(member, isTranslation ? SubtitleOrigin.Translated : SubtitleOrigin.Automatic);
        }

        return tracks;

        void AddTrack(JsonProperty member, SubtitleOrigin origin)
        {
            if (member.Value.ValueKind != JsonValueKind.Array) return;

            var formats = member.Value.EnumerateArray()
                .Select(f => f.String("ext"))
                .Where(e => e is not null)
                .Select(e => e!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (formats.Count == 0) return;

            // A language can appear as both authored and automatic; the authored one wins.
            if (tracks.Any(t => t.LanguageCode.Equals(member.Name, StringComparison.OrdinalIgnoreCase)))
                return;

            var name = member.Value.EnumerateArray()
                .Select(f => f.String("name"))
                .FirstOrDefault(n => n is not null);

            tracks.Add(new SubtitleTrack
            {
                LanguageCode = member.Name,
                LanguageName = name,
                Origin = origin,
                AvailableFormats = formats,
            });
        }
    }

    private static List<ThumbnailInfo> MapThumbnails(JsonElement root)
    {
        var thumbnails = root.Array("thumbnails")
            .Select(t => new
            {
                Url = t.String("url"),
                Id = t.String("id"),
                Width = t.Int("width"),
                Height = t.Int("height"),
                Preference = t.Int("preference"),
            })
            .Where(t => t.Url is not null)
            .Select(t => new ThumbnailInfo
            {
                Url = t.Url!,
                Id = t.Id,
                Width = t.Width,
                Height = t.Height,
                Preference = t.Preference,
            })
            .ToList();

        // Some responses carry only the single "thumbnail" string.
        if (thumbnails.Count == 0 && root.String("thumbnail") is { } single)
            thumbnails.Add(new ThumbnailInfo { Url = single });

        return thumbnails;
    }

    /// <summary>
    /// Reads the music tags the extractor supplied. Absent fields stay null: the app never
    /// invents an artist from the channel name or a year from the upload date, because a guessed
    /// tag is worse than a missing one once it is written into a library.
    /// </summary>
    private static MusicMetadata MapMusic(JsonElement root, string webpageUrl, string id)
    {
        var releaseYear = root.Int("release_year");
        var releaseDate = root.String("release_date");

        return new MusicMetadata
        {
            Title = root.String("track") ?? root.String("title"),
            Artist = root.String("artist") ?? root.String("creator"),
            AlbumArtist = root.String("album_artist") ?? root.String("artist"),
            Album = root.String("album"),
            TrackNumber = root.Int("track_number"),
            DiscNumber = root.Int("disc_number"),
            Year = releaseYear,
            ReleaseDate = releaseDate,
            Genre = root.String("genre"),
            Composer = root.String("composer"),
            Copyright = root.String("copyright"),
            SourceUrl = webpageUrl,
            SourceId = id,
        };
    }

    /// <summary>Maps a playlist/album/channel payload, including its entries.</summary>
    public static MediaCollection MapCollection(JsonElement root)
    {
        var entries = new List<MediaItem>();
        var index = 1;

        foreach (var entry in root.Array("entries"))
        {
            // Nested playlists appear when a channel URL expands into its tabs; flatten one level.
            if (IsCollection(entry))
            {
                foreach (var nested in entry.Array("entries"))
                {
                    if (nested.ValueKind != JsonValueKind.Object) continue;
                    entries.Add(MapItem(nested, index++));
                }
                continue;
            }

            if (entry.ValueKind != JsonValueKind.Object) continue;
            entries.Add(MapItem(entry, index++));
        }

        var id = root.String("id") ?? string.Empty;
        var kind = ClassifyCollection(root, entries);

        return new MediaCollection
        {
            Id = id,
            Title = root.String("title") ?? root.String("playlist_title") ?? id,
            WebpageUrl = root.String("webpage_url") ?? root.String("original_url") ?? string.Empty,
            Kind = kind,
            Uploader = root.String("uploader"),
            ChannelName = root.String("channel") ?? root.String("uploader"),
            Description = root.String("description"),
            AlbumArtist = root.String("album_artist") ?? root.String("artist") ?? root.String("uploader"),
            Year = root.Int("release_year"),
            Thumbnails = MapThumbnails(root),
            Items = entries,
            DeclaredCount = root.Int("playlist_count"),
        };
    }

    private static MediaKind ClassifyCollection(JsonElement root, List<MediaItem> entries)
    {
        var id = root.String("id") ?? string.Empty;

        if (id.StartsWith("OLAK5uy_", StringComparison.Ordinal) ||
            id.StartsWith("MPREb", StringComparison.Ordinal))
            return MediaKind.Album;

        var extractor = root.String("extractor_key") ?? root.String("extractor") ?? string.Empty;
        if (extractor.Contains("channel", StringComparison.OrdinalIgnoreCase) ||
            extractor.Contains("tab", StringComparison.OrdinalIgnoreCase))
            return MediaKind.Channel;

        // A playlist whose entries all carry album tags is an album in everything but its id.
        if (entries.Count > 0 && entries.All(e => e.Music?.Album is not null) &&
            entries.Select(e => e.Music!.Album).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
            return MediaKind.Album;

        return MediaKind.Playlist;
    }
}

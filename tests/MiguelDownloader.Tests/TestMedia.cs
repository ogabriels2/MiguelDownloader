using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Tests;

/// <summary>
/// Builders for test fixtures.
/// <para>
/// The format shapes here mirror what YouTube actually publishes, including the details that
/// matter to the selection logic: separate video-only and audio-only streams, the "-drc" audio
/// twins, storyboard entries, and H.264 topping out at 1080p while VP9 and AV1 go higher.
/// </para>
/// </summary>
internal static class TestMedia
{
    public static MediaFormat VideoFormat(
        string id, int height, double fps = 30, VideoCodec codec = VideoCodec.Vp9,
        string ext = "webm", double? bitrate = null, long? size = null,
        string dynamicRange = "SDR") => new()
    {
        FormatId = id,
        Extension = ext,
        RawVideoCodec = codec.ToString().ToLowerInvariant(),
        RawAudioCodec = "none",
        VideoCodec = codec,
        AudioCodec = AudioCodec.None,
        Width = height * 16 / 9,
        Height = height,
        Fps = fps,
        VideoBitrate = bitrate ?? height * 2.5,
        TotalBitrate = bitrate ?? height * 2.5,
        FileSize = size,
        DynamicRange = dynamicRange,
        Protocol = "https",
    };

    public static MediaFormat AudioFormat(
        string id, double bitrate, AudioCodec codec = AudioCodec.Opus,
        string ext = "webm", string? language = null, int channels = 2,
        int sampleRate = 48000, long? size = null, string? note = null) => new()
    {
        FormatId = id,
        Extension = ext,
        RawVideoCodec = "none",
        RawAudioCodec = codec.ToString().ToLowerInvariant(),
        VideoCodec = VideoCodec.None,
        AudioCodec = codec,
        AudioBitrate = bitrate,
        TotalBitrate = bitrate,
        SampleRate = sampleRate,
        AudioChannels = channels,
        Language = language,
        FileSize = size,
        FormatNote = note,
        Protocol = "https",
    };

    public static MediaFormat MuxedFormat(string id, int height, AudioCodec audio = AudioCodec.Aac) => new()
    {
        FormatId = id,
        Extension = "mp4",
        RawVideoCodec = "avc1.4d401e",
        RawAudioCodec = "mp4a.40.2",
        VideoCodec = VideoCodec.H264,
        AudioCodec = audio,
        Width = height * 16 / 9,
        Height = height,
        Fps = 30,
        AudioBitrate = 96,
        Protocol = "https",
    };

    public static MediaFormat Storyboard(string id) => new()
    {
        FormatId = id,
        Extension = "mhtml",
        RawVideoCodec = "none",
        RawAudioCodec = "none",
        VideoCodec = VideoCodec.None,
        AudioCodec = AudioCodec.None,
        Protocol = "mhtml",
    };

    public static MediaFormat DrmFormat(string id, int height) =>
        VideoFormat(id, height) with { HasDrm = true };

    /// <summary>
    /// A typical YouTube video: storyboards, muxed legacy streams, separate video renditions in
    /// three codecs, and audio in both Opus and AAC with the DRC twins alongside.
    /// </summary>
    public static MediaItem StandardVideo(string title = "Test Video") => new()
    {
        Id = "dQw4w9WgXcQ",
        Title = title,
        WebpageUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
        ChannelName = "Test Channel",
        Uploader = "Test Channel",
        Duration = TimeSpan.FromMinutes(5),
        Kind = MediaKind.Video,
        Formats =
        [
            Storyboard("sb0"),
            MuxedFormat("18", 360),

            // H.264 stops at 1080p, exactly as YouTube publishes it.
            VideoFormat("137", 1080, 30, VideoCodec.H264, "mp4"),
            VideoFormat("136", 720, 30, VideoCodec.H264, "mp4"),
            VideoFormat("135", 480, 30, VideoCodec.H264, "mp4"),

            VideoFormat("315", 2160, 60, VideoCodec.Vp9),
            VideoFormat("308", 1440, 60, VideoCodec.Vp9),
            VideoFormat("303", 1080, 60, VideoCodec.Vp9),
            VideoFormat("248", 1080, 30, VideoCodec.Vp9),
            VideoFormat("247", 720, 30, VideoCodec.Vp9),
            VideoFormat("244", 480, 30, VideoCodec.Vp9),
            VideoFormat("243", 360, 30, VideoCodec.Vp9),
            VideoFormat("242", 240, 30, VideoCodec.Vp9),

            VideoFormat("401", 2160, 60, VideoCodec.Av1, "mp4"),
            VideoFormat("399", 1080, 60, VideoCodec.Av1, "mp4"),

            AudioFormat("251", 129, AudioCodec.Opus),
            AudioFormat("250", 65, AudioCodec.Opus),
            AudioFormat("249", 50, AudioCodec.Opus),
            AudioFormat("140", 128, AudioCodec.Aac, "m4a", sampleRate: 44100),
            AudioFormat("251-drc", 129, AudioCodec.Opus, note: "medium, DRC"),
            AudioFormat("140-drc", 128, AudioCodec.Aac, "m4a", note: "medium, DRC"),
        ],
    };

    /// <summary>A video carrying dubbed audio tracks in several languages.</summary>
    public static MediaItem MultiLanguageVideo() => StandardVideo("Multi Language") with
    {
        Formats =
        [
            VideoFormat("137", 1080, 30, VideoCodec.H264, "mp4"),
            VideoFormat("248", 1080, 30, VideoCodec.Vp9),

            AudioFormat("140-0", 128, AudioCodec.Aac, "m4a", language: "en"),
            AudioFormat("140-1", 128, AudioCodec.Aac, "m4a", language: "pt-BR"),
            AudioFormat("140-2", 128, AudioCodec.Aac, "m4a", language: "es"),
            AudioFormat("251-0", 129, AudioCodec.Opus, language: "en"),
            AudioFormat("251-1", 129, AudioCodec.Opus, language: "pt-BR"),
        ],
    };

    /// <summary>An HDR video, used to check HDR is preferred but never forced through a transcode.</summary>
    public static MediaItem HdrVideo() => StandardVideo("HDR Video") with
    {
        Formats =
        [
            VideoFormat("337", 2160, 60, VideoCodec.Vp9, dynamicRange: "HDR10"),
            VideoFormat("315", 2160, 60, VideoCodec.Vp9),
            VideoFormat("137", 1080, 30, VideoCodec.H264, "mp4"),
            AudioFormat("251", 129, AudioCodec.Opus),
        ],
    };

    /// <summary>A video offering nothing but a single pre-muxed stream.</summary>
    public static MediaItem MuxedOnlyVideo() => StandardVideo("Muxed Only") with
    {
        Formats = [MuxedFormat("18", 360)],
    };

    /// <summary>
    /// The shape TikTok publishes, transcribed from a real listing: every video rendition already
    /// carries its audio, and alongside them sits one standalone audio track.
    /// <para>
    /// This combination does not occur on YouTube, where a source that separates audio separates
    /// video too — which is why pairing the muxed video with the loose audio track went unnoticed
    /// until the app was pointed at another site.
    /// </para>
    /// </summary>
    public static MediaItem MuxedWithSeparateAudioTrack() => new()
    {
        Id = "7047596209028074758",
        Title = "TikTok video",
        WebpageUrl = "https://www.tiktok.com/@hankgreen1/video/7047596209028074758",
        ChannelName = "hankgreen1",
        Uploader = "hankgreen1",
        Duration = TimeSpan.FromSeconds(21),
        Kind = MediaKind.Video,
        Formats =
        [
            AudioFormat("audio", 128, AudioCodec.Mp3, "mp3"),
            MuxedFormat("h264_540p_815015-0", 1024) with { VideoBitrate = 815, TotalBitrate = 815 },
            MuxedFormat("h264_540p_1895168-0", 1024) with { VideoBitrate = 1895, TotalBitrate = 1895 },
            MuxedFormat("bytevc1_540p_668450-0", 1024) with
            {
                VideoCodec = VideoCodec.H265,
                RawVideoCodec = "hev1.1.6.L93.B0",
                VideoBitrate = 668,
                TotalBitrate = 668,
            },
        ],
    };

    /// <summary>
    /// The shape X publishes, transcribed field by field from a real answer.
    /// <para>
    /// Two things here do not occur on YouTube. The audio renditions name no codec at all -- not
    /// "unknown", the field is simply absent -- and are recognisable only by their bitrate. The
    /// progressive renditions name neither codec while still reporting a frame size, so nothing
    /// says whether they carry sound.
    /// </para>
    /// </summary>
    public static MediaItem UnreportedCodecs() => new()
    {
        Id = "1001551417340022785",
        Title = "Post with video",
        WebpageUrl = "https://twitter.com/LisPower1/status/1001551623938805763",
        ChannelName = "Lis Power",
        Uploader = "Lis Power",
        Duration = TimeSpan.FromSeconds(111),
        Kind = MediaKind.Video,
        Formats =
        [
            // Audio: vcodec is explicitly none, acodec absent, bitrate reported.
            new MediaFormat
            {
                FormatId = "hls-audio-32000-Audio", Extension = "mp4", Protocol = "m3u8_native",
                RawVideoCodec = "none", RawAudioCodec = null,
                VideoCodec = VideoCodec.None, AudioCodec = AudioCodec.None,
                AudioBitrate = 32, TotalBitrate = 32,
            },
            new MediaFormat
            {
                FormatId = "hls-audio-64000-Audio", Extension = "mp4", Protocol = "m3u8_native",
                RawVideoCodec = "none", RawAudioCodec = null,
                VideoCodec = VideoCodec.None, AudioCodec = AudioCodec.None,
                AudioBitrate = 64, TotalBitrate = 64,
            },

            // Progressive: neither codec reported, but a frame size is.
            new MediaFormat
            {
                FormatId = "http-256", Extension = "mp4", Protocol = "https",
                RawVideoCodec = null, RawAudioCodec = null,
                VideoCodec = VideoCodec.None, AudioCodec = AudioCodec.None,
                Width = 240, Height = 180, TotalBitrate = 256,
            },
            new MediaFormat
            {
                FormatId = "http-832", Extension = "mp4", Protocol = "https",
                RawVideoCodec = null, RawAudioCodec = null,
                VideoCodec = VideoCodec.None, AudioCodec = AudioCodec.None,
                Width = 480, Height = 360, TotalBitrate = 832,
            },

            // Video: fully described, and the lower bitrate of the pair at each size.
            new MediaFormat
            {
                FormatId = "hls-126", Extension = "mp4", Protocol = "m3u8_native",
                RawVideoCodec = "avc1.42C00C", RawAudioCodec = "none",
                VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                Width = 240, Height = 180, VideoBitrate = 126, TotalBitrate = 126,
            },
            new MediaFormat
            {
                FormatId = "hls-348", Extension = "mp4", Protocol = "m3u8_native",
                RawVideoCodec = "avc1.42C01E", RawAudioCodec = "none",
                VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.None,
                Width = 480, Height = 360, VideoBitrate = 348, TotalBitrate = 348,
            },
        ],
    };

    public static MediaItem MusicTrack() => new()
    {
        Id = "musicId1234",
        Title = "Song Title",
        WebpageUrl = "https://music.youtube.com/watch?v=musicId1234",
        ChannelName = "The Artist",
        Duration = TimeSpan.FromMinutes(3),
        Kind = MediaKind.Music,
        Music = new MusicMetadata
        {
            Title = "Song Title",
            Artist = "The Artist",
            Album = "The Album",
            AlbumArtist = "The Artist",
            TrackNumber = 3,
            Year = 2021,
        },
        Formats =
        [
            AudioFormat("251", 129, AudioCodec.Opus),
            AudioFormat("140", 128, AudioCodec.Aac, "m4a", sampleRate: 44100),
            AudioFormat("249", 50, AudioCodec.Opus),
        ],
    };
}

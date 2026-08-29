using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>
/// Which codecs each container can legitimately carry.
/// <para>
/// These tables are intentionally conservative. A few pairings are technically expressible but
/// play back badly in the wild (VP9 or Opus inside MP4), and shipping a file that half the
/// world's players refuse is worse than choosing a container that simply works.
/// </para>
/// </summary>
public static class ContainerCompatibility
{
    public static bool Supports(ContainerFormat container, VideoCodec codec) => container switch
    {
        ContainerFormat.Mkv => codec != VideoCodec.None,
        ContainerFormat.Mp4 => codec is VideoCodec.H264 or VideoCodec.H265 or VideoCodec.Av1,
        ContainerFormat.WebM => codec is VideoCodec.Vp8 or VideoCodec.Vp9 or VideoCodec.Av1,
        _ => true,
    };

    public static bool Supports(ContainerFormat container, AudioCodec codec) => container switch
    {
        ContainerFormat.Mkv => codec != AudioCodec.None,
        ContainerFormat.Mp4 => codec is AudioCodec.Aac or AudioCodec.Ac3 or AudioCodec.EAc3
            or AudioCodec.Alac or AudioCodec.Mp3,
        ContainerFormat.WebM => codec is AudioCodec.Opus or AudioCodec.Vorbis,
        _ => true,
    };

    /// <summary>MKV is the only container that carries many audio tracks without compatibility pain.</summary>
    public static bool SupportsMultipleAudioTracks(ContainerFormat container)
        => container is ContainerFormat.Mkv;

    /// <summary>MP4 can only hold mov_text, so SRT/ASS must be converted on the way in.</summary>
    public static bool SupportsTextSubtitlesNatively(ContainerFormat container)
        => container is ContainerFormat.Mkv;

    public static bool SupportsEmbeddedSubtitles(ContainerFormat container)
        => container is ContainerFormat.Mkv or ContainerFormat.Mp4;

    /// <summary>WebM has no broadly-supported cover art atom; MP4 and MKV do.</summary>
    public static bool SupportsEmbeddedCoverArt(ContainerFormat container)
        => container is ContainerFormat.Mp4 or ContainerFormat.Mkv;

    public static string Extension(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => "mp4",
        ContainerFormat.Mkv => "mkv",
        ContainerFormat.WebM => "webm",
        _ => "mkv",
    };
}

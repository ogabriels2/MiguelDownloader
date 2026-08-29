namespace MiguelDownloader.Core.Models;

/// <summary>Normalised video codec families. Raw codec strings stay on the format for the advanced view.</summary>
public enum VideoCodec
{
    None = 0,
    Unknown,
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
}

/// <summary>Normalised audio codec families.</summary>
public enum AudioCodec
{
    None = 0,
    Unknown,
    Aac,
    Opus,
    Vorbis,
    Mp3,
    Flac,
    Alac,
    Ac3,
    EAc3,
    Pcm,
}

/// <summary>Maps the raw codec strings yt-dlp reports onto the families above.</summary>
public static class CodecParser
{
    public static VideoCodec ParseVideo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            return VideoCodec.None;

        var v = raw.ToLowerInvariant();
        if (v.StartsWith("av01", StringComparison.Ordinal) || v.StartsWith("av1", StringComparison.Ordinal))
            return VideoCodec.Av1;
        if (v.StartsWith("vp09", StringComparison.Ordinal) || v.StartsWith("vp9", StringComparison.Ordinal))
            return VideoCodec.Vp9;
        if (v.StartsWith("vp08", StringComparison.Ordinal) || v.StartsWith("vp8", StringComparison.Ordinal))
            return VideoCodec.Vp8;
        if (v.StartsWith("avc", StringComparison.Ordinal) || v.StartsWith("h264", StringComparison.Ordinal) ||
            v.StartsWith("h.264", StringComparison.Ordinal))
            return VideoCodec.H264;
        if (v.StartsWith("hev", StringComparison.Ordinal) || v.StartsWith("hvc", StringComparison.Ordinal) ||
            v.StartsWith("h265", StringComparison.Ordinal))
            return VideoCodec.H265;
        return VideoCodec.Unknown;
    }

    public static AudioCodec ParseAudio(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.None;

        var a = raw.ToLowerInvariant();
        if (a.StartsWith("mp4a", StringComparison.Ordinal) || a.StartsWith("aac", StringComparison.Ordinal))
            return AudioCodec.Aac;
        if (a.StartsWith("opus", StringComparison.Ordinal)) return AudioCodec.Opus;
        if (a.StartsWith("vorbis", StringComparison.Ordinal)) return AudioCodec.Vorbis;
        if (a.StartsWith("mp3", StringComparison.Ordinal)) return AudioCodec.Mp3;
        if (a.StartsWith("flac", StringComparison.Ordinal)) return AudioCodec.Flac;
        if (a.StartsWith("alac", StringComparison.Ordinal)) return AudioCodec.Alac;
        if (a.StartsWith("ec-3", StringComparison.Ordinal) || a.StartsWith("eac3", StringComparison.Ordinal))
            return AudioCodec.EAc3;
        if (a.StartsWith("ac-3", StringComparison.Ordinal) || a.StartsWith("ac3", StringComparison.Ordinal))
            return AudioCodec.Ac3;
        if (a.StartsWith("pcm", StringComparison.Ordinal)) return AudioCodec.Pcm;
        return AudioCodec.Unknown;
    }

    /// <summary>Short label for the UI. Deliberately the familiar name, not the raw fourcc.</summary>
    public static string Label(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.H265 => "HEVC",
        VideoCodec.Vp8 => "VP8",
        VideoCodec.Vp9 => "VP9",
        VideoCodec.Av1 => "AV1",
        VideoCodec.None => "-",
        _ => "?",
    };

    public static string Label(AudioCodec codec) => codec switch
    {
        AudioCodec.Aac => "AAC",
        AudioCodec.Opus => "Opus",
        AudioCodec.Vorbis => "Vorbis",
        AudioCodec.Mp3 => "MP3",
        AudioCodec.Flac => "FLAC",
        AudioCodec.Alac => "ALAC",
        AudioCodec.Ac3 => "AC-3",
        AudioCodec.EAc3 => "E-AC-3",
        AudioCodec.Pcm => "PCM",
        AudioCodec.None => "-",
        _ => "?",
    };
}

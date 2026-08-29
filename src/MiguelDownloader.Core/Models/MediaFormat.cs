namespace MiguelDownloader.Core.Models;

/// <summary>
/// One selectable stream as reported by the extractor.
/// <para>
/// Every field is either something the source actually reported or <see langword="null"/>.
/// Nothing here is invented or rounded up: if YouTube does not report a bitrate, we show no
/// bitrate rather than a plausible-looking guess.
/// </para>
/// </summary>
public sealed record MediaFormat
{
    public required string FormatId { get; init; }

    /// <summary>Container extension the source advertises (mp4, webm, m4a...).</summary>
    public required string Extension { get; init; }

    public string? RawVideoCodec { get; init; }
    public string? RawAudioCodec { get; init; }

    public VideoCodec VideoCodec { get; init; }
    public AudioCodec AudioCodec { get; init; }

    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }

    /// <summary>"SDR", "HDR10", "HLG"... exactly as reported.</summary>
    public string? DynamicRange { get; init; }

    /// <summary>Total bitrate in kbit/s.</summary>
    public double? TotalBitrate { get; init; }
    public double? VideoBitrate { get; init; }
    public double? AudioBitrate { get; init; }

    /// <summary>Audio sample rate in Hz.</summary>
    public int? SampleRate { get; init; }
    public int? AudioChannels { get; init; }

    /// <summary>Exact size when known.</summary>
    public long? FileSize { get; init; }
    /// <summary>Estimated size, used only when <see cref="FileSize"/> is unknown.</summary>
    public long? FileSizeApprox { get; init; }

    /// <summary>BCP-47-ish language tag for this audio track, when the source declares one.</summary>
    public string? Language { get; init; }
    public string? FormatNote { get; init; }
    public string? Protocol { get; init; }

    /// <summary>The source flags this stream as DRM-protected. We never offer these.</summary>
    public bool HasDrm { get; init; }

    /// <summary>
    /// True when this stream carries picture.
    /// <para>
    /// The codec alone is not enough. X reports no codec at all for its progressive renditions --
    /// not "unknown", simply absent -- while still reporting a resolution, and a format judged
    /// solely by its codec string would be discarded as carrying nothing. A reported frame size
    /// is proof of video whatever the codec field says.
    /// </para>
    /// </summary>
    public bool HasVideo => VideoCodec != VideoCodec.None || Height is > 0 || Width is > 0;

    /// <summary>
    /// True when this stream carries sound. A reported audio bitrate is proof of it, which is how
    /// X's audio renditions are recognised: they name no codec but do report one.
    /// </summary>
    public bool HasAudio => AudioCodec != AudioCodec.None || AudioBitrate is > 0;

    /// <summary>
    /// True when the source actually said what this stream contains, rather than leaving it to be
    /// inferred.
    /// <para>
    /// A format with no codec fields at all could be video, or video with sound, and the two call
    /// for opposite handling: pair it with a separate audio track and a progressive file ends up
    /// with two soundtracks; take it alone and a video-only one ends up silent. Where the source
    /// has described its formats properly, those are preferred and the guesswork never arises.
    /// </para>
    /// </summary>
    public bool HasKnownComposition =>
        !string.IsNullOrWhiteSpace(RawVideoCodec) && !string.IsNullOrWhiteSpace(RawAudioCodec);

    /// <summary>Video and audio already together in one stream.</summary>
    public bool IsMuxed => HasVideo && HasAudio;
    public bool IsVideoOnly => HasVideo && !HasAudio;
    public bool IsAudioOnly => HasAudio && !HasVideo;

    /// <summary>
    /// YouTube publishes "-drc" twins with dynamic range compression applied. They are processed
    /// audio, not the original master, so they are hidden unless the user asks for them.
    /// </summary>
    public bool IsDrcVariant =>
        FormatId.EndsWith("-drc", StringComparison.OrdinalIgnoreCase) ||
        (FormatNote?.Contains("DRC", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Storyboard/preview streams that are never a real download target.</summary>
    public bool IsStoryboard =>
        string.Equals(Protocol, "mhtml", StringComparison.OrdinalIgnoreCase) ||
        (!HasVideo && !HasAudio);

    /// <summary>Best size figure we have, exact preferred over estimate. Null when neither is known.</summary>
    public long? BestKnownSize => FileSize ?? FileSizeApprox;

    /// <summary>True when the size is an estimate rather than a reported exact value.</summary>
    public bool SizeIsEstimate => FileSize is null && FileSizeApprox is not null;

    public bool IsHdr =>
        DynamicRange is not null &&
        !DynamicRange.Equals("SDR", StringComparison.OrdinalIgnoreCase) &&
        DynamicRange.Length > 0;

    /// <summary>e.g. "1080p60". Falls back to the raw resolution when height is unknown.</summary>
    public string QualityLabel
    {
        get
        {
            if (Height is null or 0) return IsAudioOnly ? "audio" : "?";
            var fps = Fps is > 0 ? (int)Math.Round(Fps.Value) : 0;
            return fps > 30 ? $"{Height}p{fps}" : $"{Height}p";
        }
    }

    public string Resolution => Width is > 0 && Height is > 0 ? $"{Width}x{Height}" : "-";
}

namespace MiguelDownloader.Core.Formats;

/// <summary>
/// Friendly quality choices for people who do not want to think about codecs.
/// Each maps onto a concrete rule over the formats the source actually offers.
/// </summary>
public enum QualityTier
{
    /// <summary>Highest resolution and bitrate available, whatever the codec.</summary>
    Best = 0,
    /// <summary>Capped at 1080p, which covers most screens at a fraction of the size.</summary>
    High,
    /// <summary>Capped at 720p. A sensible default when size matters as much as detail.</summary>
    Balanced,
    /// <summary>The smallest watchable option the source offers.</summary>
    Smallest,
    /// <summary>The user pinned exact formats; the tier rules do not apply.</summary>
    Custom,
}

/// <summary>Which video codec to lean towards when several encode the same resolution.</summary>
public enum VideoCodecPreference
{
    /// <summary>
    /// Balance quality, size and playability: prefer AV1/VP9 for their efficiency, but never at
    /// the cost of dropping to a lower resolution.
    /// </summary>
    Auto = 0,
    /// <summary>Best compression per bit. Newer hardware decodes it; older may fall back to software.</summary>
    Av1,
    /// <summary>Efficient and very widely supported.</summary>
    Vp9,
    /// <summary>Plays on essentially everything, at the cost of larger files.</summary>
    H264,
}

/// <summary>Which audio codec to lean towards when both are offered at similar bitrates.</summary>
public enum AudioCodecPreference
{
    /// <summary>Prefer the highest-bitrate track, whatever the codec.</summary>
    Auto = 0,
    /// <summary>Prefer Opus, which sounds better per bit than AAC at YouTube bitrates.</summary>
    Opus,
    /// <summary>Prefer AAC/M4A, which more devices and editors accept.</summary>
    Aac,
}

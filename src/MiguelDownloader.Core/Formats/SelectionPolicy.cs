namespace MiguelDownloader.Core.Formats;

/// <summary>
/// What the selection should optimise for.
/// <para>
/// This is deliberately separate from the resolution ceiling. A ceiling says "no more than
/// 1080p"; the priority says what to do when several 1080p renditions exist, and the right answer
/// genuinely differs by intent. Ranking rules that are correct for "best quality" are wrong for
/// "most compatible", so there is no single global ordering.
/// </para>
/// </summary>
public enum FormatPriority
{
    /// <summary>
    /// The best picture the source actually has: resolution first, then dynamic range, frame
    /// rate, and bitrate. Codec matters only as a tie-breaker.
    /// </summary>
    Quality = 0,

    /// <summary>
    /// The result most likely to play anywhere: H.264 and AAC in MP4, SDR, and no re-encoding.
    /// Resolution and frame rate still matter, but only after playability.
    /// </summary>
    Compatibility,

    /// <summary>
    /// The smallest acceptable file: efficient codecs and low reported size, without dropping
    /// below a watchable resolution.
    /// </summary>
    Size,
}

/// <summary>
/// How to treat high dynamic range.
/// <para>
/// A boolean could not express the difference between "leave it alone" and "actively avoid it",
/// and those are different requests. Preserving HDR is the default because discarding it is a
/// real loss of picture information that cannot be recovered afterwards.
/// </para>
/// </summary>
public enum HdrPolicy
{
    /// <summary>
    /// Take whatever the best stream happens to be, HDR or not. Dynamic range is not used as a
    /// ranking signal, so nothing is gained or lost on its account.
    /// </summary>
    Auto = 0,

    /// <summary>Prefer an HDR rendition when the source publishes one at the chosen quality.</summary>
    PreferHdr,

    /// <summary>
    /// Prefer SDR. Used by the compatibility preset, where an HDR stream would either need
    /// tone-mapping (a lossy re-encode) or produce a washed-out file on SDR playback paths.
    /// </summary>
    PreferSdr,
}

/// <summary>What will happen to the streams on the way to the final file.</summary>
public enum ProcessingKind
{
    /// <summary>Bits copied unchanged into the same container. Nothing is decoded.</summary>
    StreamCopy = 0,

    /// <summary>Container rewritten, streams copied unchanged. No quality change.</summary>
    Remux,

    /// <summary>Decoded and re-encoded. Always lossy.</summary>
    Transcode,
}

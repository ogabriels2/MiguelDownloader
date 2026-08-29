namespace MiguelDownloader.Core.Formats;

/// <summary>How much is actually known about a size.</summary>
public enum SizeCertainty
{
    /// <summary>Nothing reported a size. No number should be shown at all.</summary>
    Unknown = 0,

    /// <summary>
    /// Every stream reported an exact byte count. This is the only state that may be presented
    /// without qualification.
    /// </summary>
    Exact,

    /// <summary>
    /// Every stream reported a size, but at least one was the source's own estimate rather than a
    /// measured value. Shown with a tilde.
    /// </summary>
    Estimated,

    /// <summary>
    /// Some streams reported no size at all. The total is a floor, not a total, and must be
    /// labelled as such: a 4K video whose size the source omits would otherwise be advertised as
    /// the size of its audio track.
    /// </summary>
    AtLeast,
}

/// <summary>
/// A size together with how much confidence it deserves.
/// <para>
/// This exists because a plain <c>long?</c> cannot distinguish "1.3 GB", "about 1.3 GB", "at
/// least 9.8 MB" and "no idea" — and presenting any of the last three as the first is a false
/// claim about the file the user is about to download.
/// </para>
/// </summary>
public readonly record struct SizeEstimate(SizeCertainty Certainty, long? Bytes)
{
    public static SizeEstimate Unknown { get; } = new(SizeCertainty.Unknown, null);

    /// <summary>True when there is a number worth showing at all.</summary>
    public bool HasValue => Bytes is > 0 && Certainty != SizeCertainty.Unknown;

    /// <summary>
    /// The value to use when reserving disk space. A floor is still useful for refusing a
    /// download that plainly cannot fit, so it is returned; the caller knows it may be low.
    /// </summary>
    public long? ForDiskCheck => Bytes is > 0 ? Bytes : null;

    /// <summary>
    /// Resource key for the label that must accompany the number, so the interface cannot show a
    /// figure without the qualification that belongs to it.
    /// </summary>
    public string LabelKey => Certainty switch
    {
        SizeCertainty.Exact => "Size_Exact",
        SizeCertainty.Estimated => "Size_Estimated",
        SizeCertainty.AtLeast => "Size_AtLeast",
        _ => "Size_Unknown",
    };

    /// <summary>
    /// The label to use given whether the pipeline will re-encode.
    /// <para>
    /// When it will, this figure describes what gets downloaded, not the file that ends up on
    /// disk: the encoder decides that, and it can differ by a factor of two. A 2:38 track fetched
    /// as a ~130 kbit/s stream and re-encoded to VBR MP3 arrives at roughly double the size, so
    /// calling the download figure "the size" would be a straightforwardly false claim.
    /// </para>
    /// </summary>
    public string LabelKeyFor(bool outputWillBeReencoded)
        => outputWillBeReencoded && HasValue ? "Size_BeforeConversion" : LabelKey;

    /// <summary>
    /// Combines the sizes of the streams that will be downloaded, degrading the certainty to
    /// match the least certain component.
    /// </summary>
    /// <param name="sizes">Exact byte count per stream, or null where none was reported.</param>
    /// <param name="anyIsApproximate">True when a reported size was the source's estimate.</param>
    public static SizeEstimate Combine(IReadOnlyList<long?> sizes, bool anyIsApproximate)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        if (sizes.Count == 0) return Unknown;

        var known = sizes.Where(s => s is > 0).Select(s => s!.Value).ToList();
        if (known.Count == 0) return Unknown;

        var total = known.Sum();

        // A missing component means the total is only a lower bound, which outranks the
        // exact/estimated distinction: the number is wrong in magnitude, not just in precision.
        if (known.Count < sizes.Count) return new SizeEstimate(SizeCertainty.AtLeast, total);

        return new SizeEstimate(
            anyIsApproximate ? SizeCertainty.Estimated : SizeCertainty.Exact,
            total);
    }
}

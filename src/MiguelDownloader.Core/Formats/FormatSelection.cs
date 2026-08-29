using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>
/// What the user asked for, before it is resolved against the formats that actually exist.
/// Resolution and fps act as ceilings, not demands: asking for 4K on a 1080p video yields 1080p
/// rather than an error, and never an upscale.
/// </summary>
public sealed record FormatSelection
{
    public QualityTier Tier { get; init; } = QualityTier.Best;

    /// <summary>
    /// What to optimise for when several formats satisfy the ceilings. Set from the preset;
    /// this is what makes "best quality" and "compatibility" rank the same formats differently.
    /// </summary>
    public FormatPriority Priority { get; init; } = FormatPriority.Quality;

    /// <summary>Ceiling on height. Null means no cap.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>Ceiling on frame rate. Null means no cap.</summary>
    public double? MaxFps { get; init; }

    public VideoCodecPreference VideoCodec { get; init; } = VideoCodecPreference.Auto;
    public AudioCodecPreference AudioCodec { get; init; } = AudioCodecPreference.Auto;

    /// <summary>How to treat high dynamic range. Preserving it is the default.</summary>
    public HdrPolicy Hdr { get; init; } = HdrPolicy.Auto;

    /// <summary>Include YouTube's dynamic-range-compressed audio twins in the candidate pool.</summary>
    public bool AllowDrcAudio { get; init; }

    /// <summary>Explicit format id, set when the user pinned one in the advanced view.</summary>
    public string? PinnedVideoFormatId { get; init; }
    public string? PinnedAudioFormatId { get; init; }

    /// <summary>
    /// Language tags of the audio tracks to keep. Empty means "just the default track".
    /// </summary>
    public IReadOnlyList<string> AudioLanguages { get; init; } = [];

    /// <summary>Take every audio track the source offers.</summary>
    public bool AllAudioLanguages { get; init; }

    public static FormatSelection ForTier(QualityTier tier) => tier switch
    {
        QualityTier.Best => new FormatSelection
        {
            Tier = tier, Priority = FormatPriority.Quality, Hdr = HdrPolicy.PreferHdr,
        },
        QualityTier.High => new FormatSelection
        {
            Tier = tier, Priority = FormatPriority.Quality, MaxHeight = 1080,
        },
        QualityTier.Balanced => new FormatSelection
        {
            Tier = tier, Priority = FormatPriority.Quality, MaxHeight = 720,
        },
        QualityTier.Smallest => new FormatSelection
        {
            Tier = tier, Priority = FormatPriority.Size,
        },
        _ => new FormatSelection { Tier = tier },
    };
}

/// <summary>The concrete streams chosen for one item, plus why.</summary>
public sealed record ResolvedFormats
{
    public MediaFormat? Video { get; init; }
    public IReadOnlyList<MediaFormat> Audio { get; init; } = [];

    /// <summary>Set when the source only offers a pre-muxed stream at this quality.</summary>
    public bool UsesMuxedSource { get; init; }

    /// <summary>Every stream this result will actually download.</summary>
    private IEnumerable<MediaFormat> AllStreams
        => (Video is null ? Enumerable.Empty<MediaFormat>() : [Video]).Concat(Audio);

    /// <summary>
    /// Combined size of the chosen streams, carrying how much confidence the number deserves.
    /// <para>
    /// Summing only the components that happen to be known and calling it a total is how a 4K
    /// download once got advertised as the size of its audio track. The certainty travels with
    /// the number so the interface cannot present a floor as a total.
    /// </para>
    /// </summary>
    public SizeEstimate Size
    {
        get
        {
            var streams = AllStreams.ToList();
            if (streams.Count == 0) return SizeEstimate.Unknown;

            return SizeEstimate.Combine(
                streams.Select(s => s.BestKnownSize).ToList(),
                streams.Any(s => s.SizeIsEstimate));
        }
    }

    /// <summary>
    /// Total in bytes when every component reported one, otherwise null. Used where only a
    /// complete figure is meaningful.
    /// </summary>
    public long? EstimatedSize
        => Size.Certainty is SizeCertainty.Exact or SizeCertainty.Estimated ? Size.Bytes : null;

    /// <summary>True when at least one component size was an estimate rather than exact.</summary>
    public bool SizeIsEstimate => Size.Certainty == SizeCertainty.Estimated;

    public bool HasAnything => Video is not null || Audio.Count > 0;
}

using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>One resolution the source genuinely offers, with what comes with it.</summary>
public sealed record ResolutionOption
{
    public required int Height { get; init; }

    /// <summary>Frame rates actually published at this height.</summary>
    public IReadOnlyList<double> FrameRates { get; init; } = [];

    /// <summary>Video codecs actually published at this height.</summary>
    public IReadOnlyList<VideoCodec> Codecs { get; init; } = [];

    public bool HasHdr { get; init; }

    /// <summary>Smallest known size across the renditions at this height, when any is known.</summary>
    public long? SmallestKnownSize { get; init; }
    public long? LargestKnownSize { get; init; }

    /// <summary>e.g. "2160p" or "1080p60" when a high frame rate is available.</summary>
    public string Label
    {
        get
        {
            var top = FrameRates.Count > 0 ? (int)Math.Round(FrameRates.Max()) : 0;
            return top > 30 ? $"{Height}p{top}" : $"{Height}p";
        }
    }

    /// <summary>Marketing shorthand people recognise, only where it genuinely applies.</summary>
    public string? CommonName => Height switch
    {
        >= 4320 => "8K",
        >= 2160 => "4K",
        >= 1440 => "2K",
        >= 1080 => "Full HD",
        >= 720 => "HD",
        _ => null,
    };
}

/// <summary>One audio track the source offers, grouped by language.</summary>
public sealed record AudioTrackOption
{
    public required string LanguageCode { get; init; }
    public string? LanguageName { get; init; }

    /// <summary>Highest bitrate published for this track, in kbit/s. Null when not reported.</summary>
    public double? BestBitrate { get; init; }

    public IReadOnlyList<AudioCodec> Codecs { get; init; } = [];
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }

    /// <summary>True when this is the only track, or the one the source treats as original.</summary>
    public bool IsDefault { get; init; }

    public string DisplayName => LanguageName is { Length: > 0 } ? LanguageName : LanguageCode;
}

/// <summary>
/// A digest of what one item actually offers, built for the UI so it can never present a
/// resolution, frame rate or codec the source does not have.
/// </summary>
public sealed record FormatCatalog
{
    public IReadOnlyList<ResolutionOption> Resolutions { get; init; } = [];
    public IReadOnlyList<AudioTrackOption> AudioTracks { get; init; } = [];

    /// <summary>Distinct video codecs across the whole item.</summary>
    public IReadOnlyList<VideoCodec> VideoCodecs { get; init; } = [];
    public IReadOnlyList<AudioCodec> AudioCodecs { get; init; } = [];

    public bool HasVideo => Resolutions.Count > 0;
    public bool HasAudio => AudioTracks.Count > 0;
    public bool HasHdr => Resolutions.Any(r => r.HasHdr);
    public bool HasMultipleAudioTracks => AudioTracks.Count > 1;

    public int? MaxHeight => Resolutions.Count > 0 ? Resolutions.Max(r => r.Height) : null;

    /// <summary>True when the item exposed nothing we can download.</summary>
    public bool IsEmpty => !HasVideo && !HasAudio;

    /// <summary>
    /// Builds the catalog from an item, ignoring storyboards, DRM-protected streams and the
    /// dynamic-range-compressed audio twins.
    /// </summary>
    public static FormatCatalog Build(MediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var pool = FormatSelector.Candidates(item, allowDrc: false).ToList();

        var resolutions = pool
            .Where(f => f.HasVideo && f.Height is > 0)
            .GroupBy(f => f.Height!.Value)
            .Select(g => new ResolutionOption
            {
                Height = g.Key,
                FrameRates = g.Select(f => f.Fps).Where(f => f is > 0).Select(f => f!.Value)
                    .Distinct().OrderBy(f => f).ToList(),
                Codecs = g.Select(f => f.VideoCodec).Distinct().OrderBy(c => c).ToList(),
                HasHdr = g.Any(f => f.IsHdr),
                SmallestKnownSize = g.Select(f => f.BestKnownSize).Where(s => s is not null)
                    .Select(s => s!.Value).DefaultIfEmpty().Min() is var min && min > 0 ? min : null,
                LargestKnownSize = g.Select(f => f.BestKnownSize).Where(s => s is not null)
                    .Select(s => s!.Value).DefaultIfEmpty().Max() is var max && max > 0 ? max : null,
            })
            .OrderByDescending(r => r.Height)
            .ToList();

        var audioGroups = pool
            .Where(f => f.IsAudioOnly)
            .GroupBy(f => FormatSelector.NormalizeLanguage(f.Language), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var audioTracks = audioGroups
            .Select(g => new AudioTrackOption
            {
                LanguageCode = g.Key,
                LanguageName = LanguageNames.Resolve(g.Key),
                BestBitrate = g.Select(f => f.AudioBitrate).Where(b => b is > 0)
                    .Select(b => b!.Value).DefaultIfEmpty().Max() is var b && b > 0 ? b : null,
                Codecs = g.Select(f => f.AudioCodec).Distinct().OrderBy(c => c).ToList(),
                Channels = g.Select(f => f.AudioChannels).Where(c => c is > 0)
                    .Select(c => c!.Value).DefaultIfEmpty().Max() is var ch && ch > 0 ? ch : null,
                SampleRate = g.Select(f => f.SampleRate).Where(s => s is > 0)
                    .Select(s => s!.Value).DefaultIfEmpty().Max() is var sr && sr > 0 ? sr : null,
                IsDefault = audioGroups.Count == 1 || g.Key.Equals("und", StringComparison.OrdinalIgnoreCase),
            })
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new FormatCatalog
        {
            Resolutions = resolutions,
            AudioTracks = audioTracks,
            VideoCodecs = pool.Where(f => f.HasVideo).Select(f => f.VideoCodec).Distinct().OrderBy(c => c).ToList(),
            AudioCodecs = pool.Where(f => f.HasAudio).Select(f => f.AudioCodec).Distinct().OrderBy(c => c).ToList(),
        };
    }
}

/// <summary>Turns language tags into names in the current UI culture, falling back to the tag.</summary>
public static class LanguageNames
{
    public static string? Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (code.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(code.Replace('_', '-'));
            return culture.IsNeutralCulture || !culture.Name.Contains('-')
                ? culture.DisplayName
                : culture.DisplayName;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }
}

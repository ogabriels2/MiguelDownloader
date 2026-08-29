using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>
/// Turns a <see cref="FormatSelection"/> into the concrete streams to download.
/// <para>
/// There is no single correct ordering of formats. Ranking that is right for "best quality" is
/// wrong for "most compatible", so the ceilings are applied first and then
/// <see cref="FormatPriority"/> decides how the survivors are ranked. Ceilings always win over
/// preference: a codec or compatibility choice can never quietly cost the user picture quality
/// beyond what they asked to give up.
/// </para>
/// <para>
/// Nothing is ever upscaled. If a ceiling sits above what the source offers, the best real
/// format is returned unchanged.
/// </para>
/// </summary>
public static class FormatSelector
{
    /// <summary>
    /// How readily a codec plays on an arbitrary machine.
    /// <para>
    /// H.264 plays everywhere. AV1 is decoded by current Windows and browsers, in software when
    /// no hardware path exists. HEVC ranks below AV1 on Windows because playback needs a codec
    /// extension most people do not have. VP8 and VP9 cannot go into MP4 at all.
    /// </para>
    /// </summary>
    private static int PlaybackCompatibility(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => 4,
        VideoCodec.Av1 => 2,
        VideoCodec.H265 => 1,
        _ => 0,
    };

    /// <summary>
    /// Compression efficiency, i.e. quality retained per byte. Used when the goal is a small file.
    /// </summary>
    private static int CodecEfficiency(VideoCodec codec) => codec switch
    {
        VideoCodec.Av1 => 4,
        VideoCodec.H265 => 3,
        VideoCodec.Vp9 => 2,
        VideoCodec.H264 => 1,
        _ => 0,
    };

    /// <summary>
    /// Default codec ranking when the user expressed no preference and quality is the goal.
    /// At 1080p and below, H.264 wins the tie: it pairs with AAC into an MP4 with no re-encoding
    /// at all. Above 1080p YouTube rarely publishes H.264, so efficiency decides instead.
    /// </summary>
    private static int QualityCodecScore(VideoCodec codec, int height) => height <= 1080
        ? PlaybackCompatibility(codec)
        : CodecEfficiency(codec);

    private static int CodecScore(VideoCodec codec, FormatSelection selection, int height)
    {
        // An explicit codec choice outranks every heuristic, but still only as a tie-break
        // among formats that already passed the ceilings.
        var explicitMatch = selection.VideoCodec switch
        {
            VideoCodecPreference.Av1 => codec == VideoCodec.Av1,
            VideoCodecPreference.Vp9 => codec == VideoCodec.Vp9,
            VideoCodecPreference.H264 => codec == VideoCodec.H264,
            _ => false,
        };
        if (explicitMatch) return 100;

        return selection.Priority switch
        {
            FormatPriority.Compatibility => PlaybackCompatibility(codec),
            FormatPriority.Size => CodecEfficiency(codec),
            _ => QualityCodecScore(codec, height),
        };
    }

    private static int CodecScore(AudioCodec codec, FormatSelection selection) => selection.AudioCodec switch
    {
        AudioCodecPreference.Opus => codec == AudioCodec.Opus ? 10 : 1,
        AudioCodecPreference.Aac => codec == AudioCodec.Aac ? 10 : 1,
        // Compatibility wants AAC, which every player and editor accepts.
        _ => selection.Priority == FormatPriority.Compatibility
            ? codec == AudioCodec.Aac ? 10 : 1
            : 1,
    };

    /// <summary>
    /// Ranks a format by dynamic range according to the policy. <see cref="HdrPolicy.Auto"/>
    /// scores everything equally, so HDR is neither sought nor avoided.
    /// </summary>
    private static int HdrScore(MediaFormat format, HdrPolicy policy) => policy switch
    {
        HdrPolicy.PreferHdr => format.IsHdr ? 1 : 0,
        HdrPolicy.PreferSdr => format.IsHdr ? 0 : 1,
        _ => 0,
    };

    /// <summary>Every format that is a legitimate download target.</summary>
    public static IEnumerable<MediaFormat> Candidates(MediaItem item, bool allowDrc)
        => item.Formats.Where(f => !f.IsStoryboard && !f.HasDrm && (allowDrc || !f.IsDrcVariant));

    /// <summary>
    /// Resolves the streams for a video download. Returns an empty result when the item exposes no
    /// usable formats, which the caller surfaces as a real error rather than a silent no-op.
    /// </summary>
    public static ResolvedFormats ResolveVideo(MediaItem item, FormatSelection selection)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(selection);

        var pool = Candidates(item, selection.AllowDrcAudio).ToList();
        if (pool.Count == 0) return new ResolvedFormats();

        var video = SelectVideoStream(pool, selection);

        // A muxed stream already carries its audio, so no separate track is fetched for it. The
        // test has to be on the chosen stream alone, not on whether the source happens to publish
        // audio separately as well: TikTok offers only muxed video *and* a standalone audio track,
        // and pairing the two asks ffmpeg to lay a second soundtrack over one that is already there.
        //
        // On YouTube this never came up, because a source that separates audio separates video too.
        var audio = video is { IsMuxed: true } ? [] : SelectAudioStreams(pool, selection);

        return new ResolvedFormats
        {
            Video = video,
            Audio = audio,
            UsesMuxedSource = video is { IsMuxed: true },
        };
    }

    /// <summary>Resolves the best audio-only streams, used by the music and audio-extraction flows.</summary>
    public static ResolvedFormats ResolveAudioOnly(MediaItem item, FormatSelection selection)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(selection);

        var pool = Candidates(item, selection.AllowDrcAudio).ToList();
        if (pool.Count == 0) return new ResolvedFormats();

        var audio = SelectAudioStreams(pool, selection);
        if (audio.Count > 0) return new ResolvedFormats { Audio = audio };

        // Nothing audio-only on offer: fall back to the best muxed stream so the audio can still
        // be extracted from it.
        var muxed = pool.Where(f => f.IsMuxed)
            .OrderByDescending(f => f.AudioBitrate ?? 0)
            .ThenByDescending(f => f.TotalBitrate ?? 0)
            .FirstOrDefault();

        return muxed is null
            ? new ResolvedFormats()
            : new ResolvedFormats { Audio = [muxed], UsesMuxedSource = true };
    }

    private static MediaFormat? SelectVideoStream(List<MediaFormat> pool, FormatSelection selection)
    {
        if (selection.PinnedVideoFormatId is { Length: > 0 } pinned)
            return pool.FirstOrDefault(f => f.FormatId == pinned);

        var withVideo = pool.Where(f => f.HasVideo).ToList();
        if (withVideo.Count == 0) return null;

        // Ceilings must never eliminate everything: when the caps exclude every format the source
        // has, fall back to the full set and take its smallest rather than failing the download.
        var capped = withVideo
            .Where(f => selection.MaxHeight is null || (f.Height ?? 0) <= selection.MaxHeight)
            .Where(f => selection.MaxFps is null || (f.Fps ?? 0) <= selection.MaxFps.Value + 0.01)
            .ToList();

        if (capped.Count == 0)
        {
            return withVideo
                .OrderBy(f => f.Height ?? int.MaxValue)
                .ThenBy(f => f.Fps ?? 0)
                .First();
        }

        return selection.Priority switch
        {
            FormatPriority.Compatibility => PickForCompatibility(capped, selection),
            FormatPriority.Size => PickForSize(capped, selection),
            _ => PickForQuality(capped, selection),
        };
    }

    /// <summary>
    /// The best picture available. Resolution dominates, then dynamic range, then frame rate,
    /// then how much data the encoder actually spent. Codec only breaks remaining ties.
    /// </summary>
    private static MediaFormat PickForQuality(List<MediaFormat> capped, FormatSelection selection)
        => capped
            .OrderByDescending(f => f.Height ?? 0)
            .ThenByDescending(f => HdrScore(f, selection.Hdr))
            .ThenByDescending(f => f.HasKnownComposition)
            .ThenByDescending(f => f.Fps ?? 0)
            .ThenByDescending(f => f.VideoBitrate ?? f.TotalBitrate ?? 0)
            .ThenByDescending(f => CodecScore(f.VideoCodec, selection, f.Height ?? 0))
            .First();

    /// <summary>
    /// The result most likely to play anywhere. Codec compatibility leads, because a pristine VP9
    /// file that a device refuses to open is worse than an H.264 one it plays. Resolution still
    /// matters, immediately after.
    /// </summary>
    private static MediaFormat PickForCompatibility(List<MediaFormat> capped, FormatSelection selection)
        => capped
            .OrderByDescending(f => CodecScore(f.VideoCodec, selection, f.Height ?? 0))
            .ThenByDescending(f => HdrScore(f, selection.Hdr))
            .ThenByDescending(f => f.HasKnownComposition)
            .ThenByDescending(f => f.Height ?? 0)
            .ThenByDescending(f => f.Fps ?? 0)
            .ThenByDescending(f => f.VideoBitrate ?? f.TotalBitrate ?? 0)
            .First();

    /// <summary>
    /// The smallest sensible file. Prefers the lowest reported size among formats that are still
    /// watchable, so "smaller" does not degenerate into a 144p thumbnail-grade stream.
    /// </summary>
    private static MediaFormat PickForSize(List<MediaFormat> capped, FormatSelection selection)
    {
        var watchable = capped.Where(f => (f.Height ?? 0) >= 360).ToList();
        var source = watchable.Count > 0 ? watchable : capped;

        return source
            .OrderBy(f => f.Height ?? int.MaxValue)
            .ThenByDescending(f => f.HasKnownComposition)
            .ThenBy(f => f.BestKnownSize ?? long.MaxValue)
            .ThenByDescending(f => CodecScore(f.VideoCodec, selection, f.Height ?? 0))
            .ThenBy(f => f.TotalBitrate ?? double.MaxValue)
            .First();
    }

    private static List<MediaFormat> SelectAudioStreams(List<MediaFormat> pool, FormatSelection selection)
    {
        if (selection.PinnedAudioFormatId is { Length: > 0 } pinned)
        {
            var exact = pool.FirstOrDefault(f => f.FormatId == pinned);
            return exact is null ? [] : [exact];
        }

        var audioOnly = pool.Where(f => f.IsAudioOnly).ToList();

        // No separate audio streams: the caller will use whatever the muxed video carries.
        if (audioOnly.Count == 0) return [];

        var byLanguage = audioOnly
            .GroupBy(f => NormalizeLanguage(f.Language), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        if (selection.AllAudioLanguages && byLanguage.Count > 1)
        {
            return byLanguage.Values
                .Select(g => BestAudio(g, selection))
                .OfType<MediaFormat>()
                .ToList();
        }

        if (selection.AudioLanguages.Count > 0 && byLanguage.Count > 1)
        {
            var picked = new List<MediaFormat>();
            foreach (var lang in selection.AudioLanguages)
            {
                var key = NormalizeLanguage(lang);
                // Match "pt" against "pt-BR" as well, so a coarse request still finds the track.
                var match = byLanguage.FirstOrDefault(kv =>
                    kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase));

                if (match.Value is { Count: > 0 } group && BestAudio(group, selection) is { } best)
                    picked.Add(best);
            }
            if (picked.Count > 0) return picked;
        }

        var single = BestAudio(audioOnly, selection);
        return single is null ? [] : [single];
    }

    private static MediaFormat? BestAudio(List<MediaFormat> group, FormatSelection selection)
    {
        // A small file wants the smallest acceptable audio; everything else wants the best.
        if (selection.Priority == FormatPriority.Size)
        {
            return group
                .OrderByDescending(f => CodecScore(f.AudioCodec, selection))
                .ThenBy(f => f.AudioBitrate ?? double.MaxValue)
                .FirstOrDefault();
        }

        return group
            .OrderByDescending(f => CodecScore(f.AudioCodec, selection))
            .ThenByDescending(f => f.AudioBitrate ?? f.TotalBitrate ?? 0)
            .ThenByDescending(f => f.AudioChannels ?? 0)
            .ThenByDescending(f => f.SampleRate ?? 0)
            .FirstOrDefault();
    }

    /// <summary>Empty or unknown language tags collapse into one bucket so they group together.</summary>
    internal static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? "und" : language.Trim();
}

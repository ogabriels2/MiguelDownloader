using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>
/// Decides how to assemble the output file.
/// <para>
/// The governing rule is simple: never re-encode when a copy will do. Re-encoding always costs
/// quality and time, so it happens only when the user explicitly pins a container that cannot
/// carry the streams they picked. When the container is left on
/// <see cref="ContainerFormat.Auto"/> the planner is guaranteed to find a lossless answer,
/// because MKV can hold anything.
/// </para>
/// </summary>
public static class MuxPlanner
{
    /// <summary>
    /// Builds the plan for a video download.
    /// </summary>
    /// <param name="video">The chosen video stream, or null for an audio-only result.</param>
    /// <param name="audioTracks">Chosen audio streams, in output order. May be empty.</param>
    /// <param name="requested">The container the user asked for.</param>
    /// <param name="embedSubtitles">Whether the user wants subtitles inside the file.</param>
    /// <param name="embedThumbnail">Whether the user wants cover art inside the file.</param>
    public static MuxPlan Plan(
        MediaFormat? video,
        IReadOnlyList<MediaFormat> audioTracks,
        ContainerFormat requested,
        bool embedSubtitles = false,
        bool embedThumbnail = false)
    {
        ArgumentNullException.ThrowIfNull(audioTracks);

        var notes = new List<MuxNote>();
        var videoCodec = video?.VideoCodec ?? VideoCodec.None;
        var audioCodecs = audioTracks.Select(a => a.AudioCodec).Distinct().ToList();
        var multiTrack = audioTracks.Count > 1;

        var container = requested;

        if (container == ContainerFormat.Auto)
        {
            container = ChooseAutomatically(videoCodec, audioCodecs, multiTrack, embedSubtitles, notes);
        }
        else if (multiTrack && !ContainerCompatibility.SupportsMultipleAudioTracks(container))
        {
            // Honouring MP4 here would mean silently dropping tracks the user explicitly asked for.
            // Upgrading the container is the only choice that keeps the request intact.
            container = ContainerFormat.Mkv;
            notes.Add(MuxNote.MultipleAudioTracksForceMkv);
        }

        var videoAction = videoCodec switch
        {
            VideoCodec.None => StreamAction.None,
            _ when ContainerCompatibility.Supports(container, videoCodec) => StreamAction.Copy,
            _ => StreamAction.Transcode,
        };
        if (videoAction == StreamAction.Transcode) notes.Add(MuxNote.VideoTranscodeRequiredByContainer);

        var audioAction = audioCodecs.Count == 0
            ? StreamAction.None
            : audioCodecs.All(c => ContainerCompatibility.Supports(container, c))
                ? StreamAction.Copy
                : StreamAction.Transcode;
        if (audioAction == StreamAction.Transcode) notes.Add(MuxNote.AudioTranscodeRequiredByContainer);

        var subtitles = ResolveSubtitles(container, embedSubtitles, notes);

        if (embedThumbnail && !ContainerCompatibility.SupportsEmbeddedCoverArt(container))
            notes.Add(MuxNote.CoverArtWrittenAsSidecar);

        if (videoAction == StreamAction.Transcode && (video?.IsHdr ?? false))
            notes.Add(MuxNote.HdrAtRiskFromTranscode);

        if (notes.Count == 0) notes.Add(MuxNote.NoReencodeNeeded);

        return new MuxPlan
        {
            Container = container,
            Video = videoAction,
            Audio = audioAction,
            Subtitles = subtitles,
            Notes = notes,
        };
    }

    /// <summary>
    /// Picks a container that needs no re-encoding, preferring the most widely playable one.
    /// MP4 first because it plays everywhere, then WebM, then MKV which accepts anything.
    /// </summary>
    private static ContainerFormat ChooseAutomatically(
        VideoCodec video, IReadOnlyList<AudioCodec> audio, bool multiTrack, bool embedSubtitles,
        List<MuxNote> notes)
    {
        if (multiTrack)
        {
            notes.Add(MuxNote.MultipleAudioTracksForceMkv);
            return ContainerFormat.Mkv;
        }

        bool Fits(ContainerFormat c) =>
            (video == VideoCodec.None || ContainerCompatibility.Supports(c, video)) &&
            audio.All(a => ContainerCompatibility.Supports(c, a));

        // Embedding subtitles in MP4 forces a mov_text conversion; MKV avoids that entirely,
        // so when subtitles are going inside the file we prefer a container that takes them as-is.
        if (embedSubtitles)
        {
            if (Fits(ContainerFormat.Mkv))
            {
                notes.Add(MuxNote.ContainerChosenForCompatibility);
                return ContainerFormat.Mkv;
            }
        }

        foreach (var candidate in (ReadOnlySpan<ContainerFormat>)
                 [ContainerFormat.Mp4, ContainerFormat.WebM, ContainerFormat.Mkv])
        {
            if (!Fits(candidate)) continue;
            notes.Add(MuxNote.ContainerChosenForCompatibility);
            return candidate;
        }

        // Unreachable in practice: MKV accepts every codec we can encounter.
        notes.Add(MuxNote.ContainerChosenForCompatibility);
        return ContainerFormat.Mkv;
    }

    private static SubtitleDisposition ResolveSubtitles(
        ContainerFormat container, bool embed, List<MuxNote> notes)
    {
        if (!embed) return SubtitleDisposition.None;

        if (!ContainerCompatibility.SupportsEmbeddedSubtitles(container))
        {
            notes.Add(MuxNote.SubtitlesWrittenAsSidecar);
            return SubtitleDisposition.Sidecar;
        }

        if (!ContainerCompatibility.SupportsTextSubtitlesNatively(container))
        {
            notes.Add(MuxNote.SubtitlesConvertedForContainer);
            return SubtitleDisposition.EmbeddedConverted;
        }

        return SubtitleDisposition.Embedded;
    }
}

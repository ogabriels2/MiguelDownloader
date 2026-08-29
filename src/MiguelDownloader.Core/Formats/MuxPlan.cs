using MiguelDownloader.Core.Models;

namespace MiguelDownloader.Core.Formats;

/// <summary>A machine-readable reason the plan turned out the way it did, so the UI can localise it.</summary>
public enum MuxNote
{
    /// <summary>Streams go into the container untouched.</summary>
    NoReencodeNeeded = 0,
    /// <summary>Container was chosen automatically because it fits the streams as-is.</summary>
    ContainerChosenForCompatibility,
    /// <summary>The requested container cannot hold this video codec, so the video must be re-encoded.</summary>
    VideoTranscodeRequiredByContainer,
    /// <summary>The requested container cannot hold this audio codec, so the audio must be re-encoded.</summary>
    AudioTranscodeRequiredByContainer,
    /// <summary>Several audio tracks were requested, which forces MKV.</summary>
    MultipleAudioTracksForceMkv,
    /// <summary>Subtitles will be converted to the only text codec the container accepts.</summary>
    SubtitlesConvertedForContainer,
    /// <summary>The container cannot embed subtitles, so they are written alongside the file.</summary>
    SubtitlesWrittenAsSidecar,
    /// <summary>The container has no reliable cover-art support, so the thumbnail is written alongside.</summary>
    CoverArtWrittenAsSidecar,
    /// <summary>HDR video is being re-encoded, which risks losing the HDR metadata.</summary>
    HdrAtRiskFromTranscode,
}

/// <summary>
/// The decision about how to assemble the final file: which container, and for each stream
/// whether it is copied or re-encoded. Produced before any work starts so the UI can warn first.
/// </summary>
public sealed record MuxPlan
{
    public required ContainerFormat Container { get; init; }
    public required StreamAction Video { get; init; }
    public required StreamAction Audio { get; init; }
    public SubtitleDisposition Subtitles { get; init; } = SubtitleDisposition.None;
    public IReadOnlyList<MuxNote> Notes { get; init; } = [];

    /// <summary>True when nothing is re-encoded: the fast, lossless path.</summary>
    public bool IsLossless => Video is not StreamAction.Transcode && Audio is not StreamAction.Transcode;

    public bool RequiresTranscode => !IsLossless;

    public string Extension => ContainerCompatibility.Extension(Container);
}

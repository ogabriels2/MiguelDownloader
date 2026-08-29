using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using Xunit;

namespace MiguelDownloader.Tests;

public class MuxPlannerTests
{
    [Fact]
    public void AutoAlwaysFindsALosslessPlan()
    {
        // Every combination the source can produce must be expressible without re-encoding,
        // because MKV accepts all of them. This is the guarantee "Auto" makes.
        VideoCodec[] videoCodecs = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Vp8, VideoCodec.Vp9, VideoCodec.Av1];
        AudioCodec[] audioCodecs = [AudioCodec.Aac, AudioCodec.Opus, AudioCodec.Vorbis, AudioCodec.Mp3];

        foreach (var video in videoCodecs)
        {
            foreach (var audio in audioCodecs)
            {
                var plan = MuxPlanner.Plan(
                    TestMedia.VideoFormat("v", 1080, codec: video),
                    [TestMedia.AudioFormat("a", 129, audio)],
                    ContainerFormat.Auto);

                Assert.True(plan.IsLossless,
                    $"{video} + {audio} was not planned losslessly (container {plan.Container})");
            }
        }
    }

    [Fact]
    public void AutoPrefersMp4WhenTheStreamsFit()
    {
        // H.264 plus AAC is the most portable result, so it should win when available.
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Auto);

        Assert.Equal(ContainerFormat.Mp4, plan.Container);
        Assert.Equal(StreamAction.Copy, plan.Video);
        Assert.Equal(StreamAction.Copy, plan.Audio);
    }

    [Fact]
    public void AutoChoosesWebMForVp9AndOpus()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.Auto);

        Assert.Equal(ContainerFormat.WebM, plan.Container);
        Assert.True(plan.IsLossless);
    }

    [Fact]
    public void AutoFallsBackToMkvForAMixedPair()
    {
        // VP9 cannot go in MP4 safely and AAC cannot go in WebM, so only MKV takes both as-is.
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Auto);

        Assert.Equal(ContainerFormat.Mkv, plan.Container);
        Assert.True(plan.IsLossless);
    }

    [Fact]
    public void PinningMp4OverVp9RequiresATranscodeAndSaysSo()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Mp4);

        Assert.Equal(ContainerFormat.Mp4, plan.Container);
        Assert.Equal(StreamAction.Transcode, plan.Video);
        Assert.Contains(MuxNote.VideoTranscodeRequiredByContainer, plan.Notes);
        Assert.True(plan.RequiresTranscode);
    }

    [Fact]
    public void PinningMp4OverOpusRequiresAnAudioTranscode()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.Mp4);

        Assert.Equal(StreamAction.Copy, plan.Video);
        Assert.Equal(StreamAction.Transcode, plan.Audio);
        Assert.Contains(MuxNote.AudioTranscodeRequiredByContainer, plan.Notes);
    }

    [Fact]
    public void MkvNeverNeedsATranscode()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 2160, codec: VideoCodec.Av1, ext: "mp4"),
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.Mkv);

        Assert.True(plan.IsLossless);
        Assert.Contains(MuxNote.NoReencodeNeeded, plan.Notes);
    }

    [Fact]
    public void SeveralAudioTracksForceMkvEvenWhenMp4WasRequested()
    {
        // Honouring MP4 would mean dropping tracks the user explicitly asked for.
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [
                TestMedia.AudioFormat("a1", 128, AudioCodec.Aac, "m4a", language: "en"),
                TestMedia.AudioFormat("a2", 128, AudioCodec.Aac, "m4a", language: "pt"),
            ],
            ContainerFormat.Mp4);

        Assert.Equal(ContainerFormat.Mkv, plan.Container);
        Assert.Contains(MuxNote.MultipleAudioTracksForceMkv, plan.Notes);
        Assert.True(plan.IsLossless);
    }

    [Fact]
    public void EmbeddingSubtitlesInMp4IsReportedAsAConversion()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Mp4,
            embedSubtitles: true);

        Assert.Equal(SubtitleDisposition.EmbeddedConverted, plan.Subtitles);
        Assert.Contains(MuxNote.SubtitlesConvertedForContainer, plan.Notes);
        // The media streams themselves are still copied.
        Assert.True(plan.IsLossless);
    }

    [Fact]
    public void WebMCannotEmbedSubtitlesSoTheyBecomeSidecars()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.WebM,
            embedSubtitles: true);

        Assert.Equal(SubtitleDisposition.Sidecar, plan.Subtitles);
        Assert.Contains(MuxNote.SubtitlesWrittenAsSidecar, plan.Notes);
    }

    [Fact]
    public void AutoPrefersMkvWhenSubtitlesAreEmbedded()
    {
        // MKV takes SRT natively, so choosing it avoids a needless mov_text conversion.
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Auto,
            embedSubtitles: true);

        Assert.Equal(ContainerFormat.Mkv, plan.Container);
        Assert.Equal(SubtitleDisposition.Embedded, plan.Subtitles);
    }

    [Fact]
    public void WarnsWhenTranscodingWouldPutHdrAtRisk()
    {
        var hdr = TestMedia.VideoFormat("v", 2160, 60, VideoCodec.Vp9, dynamicRange: "HDR10");

        var plan = MuxPlanner.Plan(
            hdr,
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.Mp4);

        Assert.Equal(StreamAction.Transcode, plan.Video);
        Assert.Contains(MuxNote.HdrAtRiskFromTranscode, plan.Notes);
    }

    [Fact]
    public void WarnsWhenCoverArtCannotBeEmbedded()
    {
        var plan = MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.WebM,
            embedThumbnail: true);

        Assert.Contains(MuxNote.CoverArtWrittenAsSidecar, plan.Notes);
    }

    [Fact]
    public void ExtensionMatchesTheChosenContainer()
    {
        Assert.Equal("mp4", MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.H264, ext: "mp4"),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Auto).Extension);

        Assert.Equal("mkv", MuxPlanner.Plan(
            TestMedia.VideoFormat("v", 1080, codec: VideoCodec.Vp9),
            [TestMedia.AudioFormat("a", 128, AudioCodec.Aac, "m4a")],
            ContainerFormat.Auto).Extension);
    }

    [Fact]
    public void HandlesAudioOnlyPlans()
    {
        var plan = MuxPlanner.Plan(
            video: null,
            [TestMedia.AudioFormat("a", 129, AudioCodec.Opus)],
            ContainerFormat.Auto);

        Assert.Equal(StreamAction.None, plan.Video);
        Assert.Equal(StreamAction.Copy, plan.Audio);
    }
}

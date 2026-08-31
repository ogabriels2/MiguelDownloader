using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.YtDlp;
using Xunit;

namespace MiguelDownloader.Tests;

public class StoreFfmpegArgumentTests
{
    [Fact]
    public void StoreMp4ConversionUsesTheLgplOpenH264Encoder()
    {
        var (request, resolved, plan) = Scenario(
            ContainerFormat.Mp4, VideoCodec.Vp9, AudioCodec.Opus);

        var args = Build(request, resolved, plan, useLgplTranscodeEncoders: true);

        AssertPair(args, "--recode-video", "mp4");
        AssertPair(args, "--postprocessor-args", "VideoConvertor+ffmpeg_o:-c:v libopenh264");
    }

    [Fact]
    public void StoreWebMConversionUsesTheLgplVp9Encoder()
    {
        var (request, resolved, plan) = Scenario(
            ContainerFormat.WebM, VideoCodec.H264, AudioCodec.Aac);

        var args = Build(request, resolved, plan, useLgplTranscodeEncoders: true);

        AssertPair(args, "--recode-video", "webm");
        AssertPair(args, "--postprocessor-args", "VideoConvertor+ffmpeg_o:-c:v libvpx-vp9");
    }

    [Fact]
    public void WebsiteBuildLeavesFfmpegEncoderSelectionUnchanged()
    {
        var (request, resolved, plan) = Scenario(
            ContainerFormat.Mp4, VideoCodec.Vp9, AudioCodec.Opus);

        var args = Build(request, resolved, plan, useLgplTranscodeEncoders: false);

        Assert.DoesNotContain("--postprocessor-args", args);
    }

    private static IReadOnlyList<string> Build(
        DownloadRequest request,
        ResolvedFormats resolved,
        MuxPlan plan,
        bool useLgplTranscodeEncoders)
        => YtDlpArguments.ForDownload(
            request, resolved, plan, @"C:\work", "video", new DownloadSettings(),
            new AdvancedSettings(), detectedJsRuntime: null, useLgplTranscodeEncoders);

    private static (DownloadRequest Request, ResolvedFormats Resolved, MuxPlan Plan) Scenario(
        ContainerFormat container,
        VideoCodec videoCodec,
        AudioCodec audioCodec)
    {
        var video = new MediaFormat
        {
            FormatId = "video",
            Extension = videoCodec == VideoCodec.Vp9 ? "webm" : "mp4",
            VideoCodec = videoCodec,
            AudioCodec = AudioCodec.None,
            Width = 1920,
            Height = 1080,
        };
        var audio = new MediaFormat
        {
            FormatId = "audio",
            Extension = audioCodec == AudioCodec.Opus ? "webm" : "m4a",
            VideoCodec = VideoCodec.None,
            AudioCodec = audioCodec,
            AudioBitrate = 128,
        };
        var resolved = new ResolvedFormats { Video = video, Audio = [audio] };
        var plan = MuxPlanner.Plan(video, [audio], container);
        var item = new MediaItem
        {
            Id = "item",
            Title = "Video",
            WebpageUrl = "https://example.com/video",
            Formats = [video, audio],
        };
        var request = new DownloadRequest
        {
            Url = item.WebpageUrl,
            Item = item,
            Container = container,
            TargetDirectory = @"C:\downloads",
            TargetFileName = $"video.{ContainerCompatibility.Extension(container)}",
        };

        Assert.True(plan.RequiresTranscode);
        return (request, resolved, plan);
    }

    private static void AssertPair(IReadOnlyList<string> args, string option, string value)
    {
        var index = args.ToList().IndexOf(option);
        Assert.True(index >= 0, $"{option} is missing");
        Assert.True(index + 1 < args.Count, $"{option} has no value");
        Assert.Equal(value, args[index + 1]);
    }
}

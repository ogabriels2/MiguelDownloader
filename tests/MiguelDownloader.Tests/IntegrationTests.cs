using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Dependencies;
using MiguelDownloader.Engine.Downloads;
using MiguelDownloader.Engine.FFmpeg;
using MiguelDownloader.Engine.Files;
using MiguelDownloader.Engine.Music;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// End-to-end tests against the real tools and the real service.
/// <para>
/// These are excluded from the default run because they need a network connection and take real
/// time. Run them with <c>dotnet test --filter Category=Integration</c>.
/// </para>
/// <para>
/// The fixture is Blender's "Big Buck Bunny", published under a Creative Commons licence, which
/// makes it appropriate material for an automated test. Only the smallest audio rendition is
/// actually transferred.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests(ITestOutputHelper output) : IDisposable
{
    private const string TestVideoUrl = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";

    private readonly ITestOutputHelper _output = output;
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "migueldownloader-tests", Guid.NewGuid().ToString("N"));

    private static ProcessRunner Runner => new(NullLogger<ProcessRunner>.Instance);

    private static async Task<ToolPaths> ResolveToolsAsync()
    {
        var locator = new ToolLocator(Runner, NullLogger<ToolLocator>.Instance);
        return await locator.ResolveAsync(new AdvancedSettings());
    }

    [Fact]
    public async Task ToolsAreAvailable()
    {
        var tools = await ResolveToolsAsync();

        _output.WriteLine($"yt-dlp   : {tools.YtDlp.Source} {tools.YtDlp.Path} ({tools.YtDlp.Version})");
        _output.WriteLine($"ffmpeg   : {tools.Ffmpeg.Source} {tools.Ffmpeg.Path}");
        _output.WriteLine($"ffprobe  : {tools.Ffprobe.Source} {tools.Ffprobe.Path}");
        _output.WriteLine($"js runtime: {tools.JsRuntime.Source} {tools.JsRuntime.Path}");

        Assert.True(tools.CanDownload, "yt-dlp was not found");
        Assert.True(tools.CanProcess, "ffmpeg was not found");
        Assert.True(tools.CanValidate, "ffprobe was not found");
    }

    [Fact]
    public async Task AnalysesARealVideoAndReportsOnlyRealFormats()
    {
        var tools = await ResolveToolsAsync();
        Assert.True(tools.CanDownload, "yt-dlp is required for this test");

        var analyzer = new MediaAnalyzer(Runner, NullLogger<MediaAnalyzer>.Instance);
        Assert.True(YouTubeUrlReader.TryParse(TestVideoUrl, out var urlInfo));

        var result = await analyzer.AnalyzeAsync(urlInfo, tools, new AdvancedSettings());

        Assert.NotNull(result.Item);
        var item = result.Item!;

        _output.WriteLine($"title   : {item.Title}");
        _output.WriteLine($"channel : {item.ChannelName}");
        _output.WriteLine($"duration: {item.Duration}");
        _output.WriteLine($"formats : {item.Formats.Count}");

        Assert.False(string.IsNullOrWhiteSpace(item.Title));
        Assert.NotNull(item.Duration);
        Assert.NotEmpty(item.Formats);

        var catalog = FormatCatalog.Build(item);
        _output.WriteLine($"resolutions: {string.Join(", ", catalog.Resolutions.Select(r => r.Label))}");
        _output.WriteLine($"audio     : {string.Join(", ", catalog.AudioTracks.Select(
            a => $"{a.DisplayName} {a.BestBitrate:F0}kbps"))}");

        Assert.NotEmpty(catalog.Resolutions);
        Assert.NotEmpty(catalog.AudioTracks);

        // Every advertised resolution must correspond to a real stream.
        foreach (var resolution in catalog.Resolutions)
        {
            Assert.Contains(item.Formats, f => f.Height == resolution.Height);
            Assert.True(resolution.Height > 0);
        }

        // YouTube does not publish audio anywhere near 320 kbit/s. If this ever fires, the claim
        // the UI makes about audio quality needs revisiting rather than the assertion relaxing.
        var bestAudio = catalog.AudioTracks.Max(a => a.BestBitrate ?? 0);
        _output.WriteLine($"best audio bitrate: {bestAudio:F1} kbps");
        Assert.True(bestAudio < 200, $"unexpectedly high audio bitrate: {bestAudio}");
    }

    [Fact]
    public async Task DownloadsValidatesAndTagsAudio()
    {
        var tools = await ResolveToolsAsync();
        Assert.True(tools.CanDownload && tools.CanValidate, "yt-dlp and ffprobe are required");

        Directory.CreateDirectory(_scratch);

        var analyzer = new MediaAnalyzer(Runner, NullLogger<MediaAnalyzer>.Instance);
        Assert.True(YouTubeUrlReader.TryParse(TestVideoUrl, out var urlInfo));

        var analysis = await analyzer.AnalyzeAsync(urlInfo, tools, new AdvancedSettings());
        var item = analysis.Item!;

        // Take the smallest audio stream so the test stays quick and light on bandwidth.
        var smallestAudio = item.Formats
            .Where(f => f.IsAudioOnly && !f.IsDrcVariant)
            .OrderBy(f => f.AudioBitrate ?? double.MaxValue)
            .First();

        _output.WriteLine($"using format {smallestAudio.FormatId} " +
                          $"({smallestAudio.AudioBitrate:F0} kbps {smallestAudio.Extension})");

        var request = new DownloadRequest
        {
            Url = item.WebpageUrl,
            Item = item,
            Mode = DownloadMode.Audio,
            Selection = new FormatSelection { PinnedAudioFormatId = smallestAudio.FormatId },
            AudioFormat = AudioOutputFormat.KeepOriginal,
            TargetDirectory = _scratch,
            TargetFileName = "integration-test-audio.m4a",
            ExistingFilePolicy = ExistingFilePolicy.Overwrite,
            EmbedMetadata = true,
        };

        var task = new DownloadTask { Id = Guid.NewGuid().ToString("N"), Request = request };

        var executor = BuildExecutor(analyzer);
        var stages = new List<DownloadStage>();

        var settings = new AppSettings
        {
            Downloads = new DownloadSettings { TemporaryFolder = Path.Combine(_scratch, "work") },
        };

        var outcome = await executor.ExecuteAsync(
            task, tools, settings,
            onProgress: p =>
            {
                if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage);
            },
            CancellationToken.None);

        _output.WriteLine($"outcome : {outcome.Status}");
        _output.WriteLine($"error   : {outcome.Error?.Kind.ToString() ?? "none"}");
        _output.WriteLine($"file    : {outcome.FilePath}");
        _output.WriteLine($"stages  : {string.Join(" -> ", stages)}");

        Assert.Equal(DownloadStatus.Completed, outcome.Status);
        Assert.NotNull(outcome.FilePath);
        Assert.True(File.Exists(outcome.FilePath), "the reported file does not exist");

        var size = new FileInfo(outcome.FilePath!).Length;
        _output.WriteLine($"size    : {size:N0} bytes");
        Assert.True(size > 10_000, "the produced file is implausibly small");

        // The pipeline reported success, so the file must genuinely be playable audio.
        var probe = new MediaProbe(Runner, NullLogger<MediaProbe>.Instance);
        var probed = await probe.ProbeAsync(tools.Ffprobe.Path!, outcome.FilePath!);

        _output.WriteLine($"probe   : readable={probed.IsReadable} duration={probed.Duration} " +
                          $"audio={probed.AudioCodec} video={probed.HasVideoStream}");

        Assert.True(probed.IsReadable);
        Assert.True(probed.HasAudioStream);
        Assert.False(probed.HasVideoStream);
        Assert.NotNull(probed.Duration);

        // The duration must match the source, which is what proves the transfer was not truncated.
        var drift = Math.Abs((probed.Duration!.Value - item.Duration!.Value).TotalSeconds);
        Assert.True(drift < 5, $"duration drifted by {drift:F1}s from the source");

        // Progress must have moved through real stages rather than jumping straight to done.
        Assert.Contains(DownloadStage.DownloadingAudio, stages);
        Assert.Contains(DownloadStage.Validating, stages);
    }

    [Fact]
    public async Task CancellationStopsTheDownloadPromptly()
    {
        var tools = await ResolveToolsAsync();
        Assert.True(tools.CanDownload, "yt-dlp is required for this test");

        Directory.CreateDirectory(_scratch);

        var analyzer = new MediaAnalyzer(Runner, NullLogger<MediaAnalyzer>.Instance);
        Assert.True(YouTubeUrlReader.TryParse(TestVideoUrl, out var urlInfo));
        var analysis = await analyzer.AnalyzeAsync(urlInfo, tools, new AdvancedSettings());
        var item = analysis.Item!;

        // A large video stream, so the transfer is still running when cancellation arrives.
        var largest = item.Formats
            .Where(f => f.IsVideoOnly)
            .OrderByDescending(f => f.Height ?? 0)
            .First();

        var request = new DownloadRequest
        {
            Url = item.WebpageUrl,
            Item = item,
            Mode = DownloadMode.Video,
            Selection = new FormatSelection { PinnedVideoFormatId = largest.FormatId },
            Container = ContainerFormat.Auto,
            TargetDirectory = _scratch,
            TargetFileName = "cancelled.mkv",
            ExistingFilePolicy = ExistingFilePolicy.Overwrite,
        };

        var task = new DownloadTask { Id = Guid.NewGuid().ToString("N"), Request = request };
        var executor = BuildExecutor(analyzer);

        using var cts = new CancellationTokenSource();
        var settings = new AppSettings
        {
            Downloads = new DownloadSettings
            {
                TemporaryFolder = Path.Combine(_scratch, "work"),
                CheckDiskSpace = false,
            },
        };

        var started = new TaskCompletionSource();
        var run = executor.ExecuteAsync(
            task, tools, settings,
            onProgress: p =>
            {
                if (p.Stage is DownloadStage.DownloadingVideo && p.DownloadedBytes > 0)
                    started.TrySetResult();
            },
            cts.Token);

        // Wait until bytes are genuinely moving before cancelling.
        var begun = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        Assert.Same(started.Task, begun);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await cts.CancelAsync();
        var outcome = await run;
        stopwatch.Stop();

        _output.WriteLine($"cancelled in {stopwatch.ElapsedMilliseconds} ms -> {outcome.Status}");

        Assert.Equal(DownloadStatus.Cancelled, outcome.Status);
        // The process tree must actually be torn down, not merely abandoned.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"cancellation took {stopwatch.Elapsed.TotalSeconds:F1}s");

        // No stray file may appear in the destination for a cancelled job.
        Assert.False(File.Exists(Path.Combine(_scratch, "cancelled.mkv")));
    }

    [Fact]
    public async Task RejectsAnUnavailableVideoWithAClearReason()
    {
        var tools = await ResolveToolsAsync();
        Assert.True(tools.CanDownload, "yt-dlp is required for this test");

        var analyzer = new MediaAnalyzer(Runner, NullLogger<MediaAnalyzer>.Instance);
        // A well-formed id that does not resolve to a real video.
        Assert.True(YouTubeUrlReader.TryParse("https://www.youtube.com/watch?v=AAAAAAAAAAA", out var urlInfo));

        var exception = await Assert.ThrowsAsync<AnalysisException>(
            () => analyzer.AnalyzeAsync(urlInfo, tools, new AdvancedSettings()));

        _output.WriteLine($"classified as {exception.Error.Kind} -> {exception.Error.MessageKey}");

        // The point is that it is diagnosed, not dumped as raw tool output.
        Assert.NotEqual(Core.Errors.DownloadErrorKind.Unknown, exception.Error.Kind);
    }

    private static DownloadExecutor BuildExecutor(MediaAnalyzer analyzer) => new(
        Runner,
        analyzer,
        new MediaProbe(Runner, NullLogger<MediaProbe>.Instance),
        new WorkspaceManager(NullLogger<WorkspaceManager>.Instance),
        new MusicTagger(NullLogger<MusicTagger>.Instance),
        NullLogger<DownloadExecutor>.Instance);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }
}

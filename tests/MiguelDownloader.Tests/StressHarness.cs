using MiguelDownloader.Engine.Downloads;
using MiguelDownloader.Engine.FFmpeg;
using MiguelDownloader.Engine.Files;
using MiguelDownloader.Engine.Music;
using MiguelDownloader.Engine.Processes;
using MiguelDownloader.Engine.YtDlp;
using Microsoft.Extensions.Logging.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// Builds engine objects for tests that measure bookkeeping rather than downloading.
/// <para>
/// The executor is real, not a stub: the stress tests care about what the queue does with
/// thousands of items, and substituting a fake would measure the fake. Nothing runs, because
/// the queue is configured with no tools, so no process is ever launched.
/// </para>
/// </summary>
internal static class StressHarness
{
    public static ProcessRunner CreateRunner() => new(NullLogger<ProcessRunner>.Instance);

    public static DownloadExecutor CreateExecutor()
    {
        var runner = CreateRunner();
        return new DownloadExecutor(
            runner,
            new MediaAnalyzer(runner, NullLogger<MediaAnalyzer>.Instance),
            new MediaProbe(runner, NullLogger<MediaProbe>.Instance),
            new WorkspaceManager(NullLogger<WorkspaceManager>.Instance),
            new MusicTagger(NullLogger<MusicTagger>.Instance),
            NullLogger<DownloadExecutor>.Instance);
    }
}

using System.IO;
using System.Net.Http;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// A collection has to arrive whole.
/// <para>
/// A playlist of a thousand tracks is an ordinary thing to paste, and handing back two hundred of
/// them without saying so is the worst possible behaviour: the result looks complete and is not.
/// </para>
/// </summary>
public class LargeCollectionTests
{
    [Fact]
    public void AnIncompleteListingSaysSo()
    {
        // IsTruncated is what tells the interface to keep going, so it has to be right whenever
        // the source reports a total larger than what arrived.
        var partial = new MediaCollection
        {
            Id = "PL", Title = "Big", WebpageUrl = "https://example/PL",
            Items = Enumerable.Range(1, 200).Select(i => new MediaItem
            {
                Id = $"v{i}", Title = $"Item {i}", WebpageUrl = $"https://example/v{i}", IsStub = true,
            }).ToList(),
            DeclaredCount = 1039,
        };

        Assert.True(partial.IsTruncated);
        Assert.Equal(200, partial.Count);
    }

    [Fact]
    public void ACompleteListingDoesNotClaimToBeTruncated()
    {
        var whole = new MediaCollection
        {
            Id = "PL", Title = "Small", WebpageUrl = "https://example/PL",
            Items = Enumerable.Range(1, 12).Select(i => new MediaItem
            {
                Id = $"v{i}", Title = $"Item {i}", WebpageUrl = $"https://example/v{i}",
            }).ToList(),
            DeclaredCount = 12,
        };

        Assert.False(whole.IsTruncated);
    }

    [Fact]
    public void NoCeilingIsTheDefault()
    {
        // The setting exists for people who want one. Nobody should have to find it to get their
        // whole playlist.
        Assert.Equal(0, new DownloadSettings().MaxCollectionItems);
    }

    [Fact]
    public void ACatalogueTooLargeToHaveArrivedWholeAdmitsIt()
    {
        var partial = new MusicCatalog
        {
            Kind = CatalogKind.Playlist,
            Title = "Thousands",
            Platform = MusicPlatform.Deezer,
            DeclaredCount = 1039,
            Tracks = [new CatalogTrack { Title = "a", Artist = "b" }],
        };

        Assert.False(partial.IsComplete);
    }
}

/// <summary>
/// The same, against the live services: a listing that stops early is only visible against a
/// collection that is genuinely bigger than one page.
/// </summary>
[Trait("Category", "Integration")]
public class LargeCollectionIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static AdvancedSettings BuildToolSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "build", "tools-cache")))
            directory = directory.Parent;

        if (directory is null) return new AdvancedSettings();

        var cache = Path.Combine(directory.FullName, "build", "tools-cache");
        return new AdvancedSettings
        {
            YtDlpPath = Path.Combine(cache, "yt-dlp.exe"),
            FfmpegPath = Path.Combine(cache, "ffmpeg.exe"),
            FfprobePath = Path.Combine(cache, "ffprobe.exe"),
            JsRuntimePath = Path.Combine(cache, "deno.exe"),
        };
    }

    [Fact]
    public async Task AYouTubePlaylistLargerThanThePageArrivesWhole()
    {
        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);
        Assert.True(tools.YtDlp.IsAvailable);

        var analyzer = new MiguelDownloader.Engine.YtDlp.MediaAnalyzer(
            runner, NullLogger<MiguelDownloader.Engine.YtDlp.MediaAnalyzer>.Instance);

        Assert.True(MediaUrlParser.TryParse(
            "https://www.youtube.com/playlist?list=PLirAqAtl_h2r5g8xGajEwdXd3x1sZh8hC", out var url));

        // First pass: capped, and it must admit the cap rather than presenting itself as whole.
        var first = await analyzer.AnalyzeAsync(
            url, tools, settings, MiguelDownloader.Engine.YtDlp.MediaAnalyzer.LargeCollectionThreshold);

        Assert.NotNull(first.Collection);
        _output.WriteLine($"first pass: {first.Collection!.Count} of {first.Collection.DeclaredCount}");
        Assert.True(first.Collection.IsTruncated);
        Assert.True(first.Collection.DeclaredCount > 200);

        // No limit: everything.
        var whole = await analyzer.AnalyzeAsync(url, tools, settings, listingLimit: null);

        Assert.NotNull(whole.Collection);
        _output.WriteLine($"complete: {whole.Collection!.Count} of {whole.Collection.DeclaredCount}");
        Assert.False(whole.Collection.IsTruncated);
        Assert.Equal(whole.Collection.DeclaredCount, whole.Collection.Count);
    }

    [Fact]
    public async Task ADeezerPlaylistIsReadPastItsFirstPage()
    {
        // Deezer answers a hundred at a time. Before pagination this returned exactly one page,
        // whatever the playlist held.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var resolver = new DeezerCatalogResolver(http, NullLogger<DeezerCatalogResolver>.Instance);

        Assert.True(MediaUrlParser.TryParse("https://www.deezer.com/playlist/1306978785", out var url));

        var catalog = await resolver.ResolveAsync(url, limit: null);

        Assert.NotNull(catalog);
        _output.WriteLine($"{catalog!.Title}: {catalog.Tracks.Count} of {catalog.DeclaredCount}");

        Assert.True(catalog.Tracks.Count > DeezerCatalogResolver.PageSize,
            $"only {catalog.Tracks.Count} came back, which is one page");
        Assert.True(catalog.IsComplete);
    }

    [Fact]
    public async Task ACeilingIsHonouredWhenTheUserSetsOne()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var resolver = new DeezerCatalogResolver(http, NullLogger<DeezerCatalogResolver>.Instance);

        Assert.True(MediaUrlParser.TryParse("https://www.deezer.com/playlist/1306978785", out var url));

        var catalog = await resolver.ResolveAsync(url, limit: 150);

        Assert.NotNull(catalog);
        _output.WriteLine($"capped at 150: {catalog!.Tracks.Count} track(s)");

        Assert.Equal(150, catalog.Tracks.Count);
        // Capped is not complete, and the interface has to be able to tell the difference.
        Assert.False(catalog.IsComplete);
    }
}

using System.IO;
using System.Net.Http;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Urls;
using MiguelDownloader.Engine.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// The catalogue readers against the real, public endpoints.
/// <para>
/// Excluded from the default run: they need a network connection, and MusicBrainz is rate limited
/// to one request a second. Run with <c>dotnet test --filter Category=Integration</c>.
/// </para>
/// <para>
/// Nothing here downloads audio. These platforms encrypt theirs, and reading a catalogue is a
/// different act from fetching a recording.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class MusicCatalogIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static CatalogService BuildService(HttpClient http) => new(
        new DeezerCatalogResolver(http, NullLogger<DeezerCatalogResolver>.Instance),
        new AppleMusicCatalogResolver(http, NullLogger<AppleMusicCatalogResolver>.Instance),
        new PublicPageReader(http, NullLogger<PublicPageReader>.Instance),
        new MusicBrainzEnricher(http, NullLogger<MusicBrainzEnricher>.Instance),
        NullLogger<CatalogService>.Instance);

    private static HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Points the locator at the tools the build fetched. Walks up from the test binary to find
    /// the repository, so this works from wherever the runner happens to put the output.
    /// </summary>
    private static MiguelDownloader.Core.Settings.AdvancedSettings BuildToolSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "build", "tools-cache")))
            directory = directory.Parent;

        if (directory is null) return new MiguelDownloader.Core.Settings.AdvancedSettings();

        var cache = Path.Combine(directory.FullName, "build", "tools-cache");
        return new MiguelDownloader.Core.Settings.AdvancedSettings
        {
            YtDlpPath = Path.Combine(cache, "yt-dlp.exe"),
            FfmpegPath = Path.Combine(cache, "ffmpeg.exe"),
            FfprobePath = Path.Combine(cache, "ffprobe.exe"),
            JsRuntimePath = Path.Combine(cache, "deno.exe"),
        };
    }

    private static MediaUrlInfo Parse(string url)
    {
        Assert.True(MediaUrlParser.TryParse(url, out var info), $"could not parse {url}");
        return info;
    }

    [Fact]
    public async Task ReadsAWholeDeezerAlbumWithIdentifiers()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/album/302127"), enrich: false);

        Assert.NotNull(catalog);
        _output.WriteLine($"{catalog!.Title} / {catalog.Artist} - {catalog.Tracks.Count} tracks");

        Assert.Equal(CatalogKind.Album, catalog.Kind);
        Assert.Equal(14, catalog.Tracks.Count);
        Assert.True(catalog.IsComplete);
        Assert.False(string.IsNullOrWhiteSpace(catalog.Label));
        Assert.False(string.IsNullOrWhiteSpace(catalog.Barcode));

        // The ISRC is what makes a match verifiable rather than a guess, so every track must have
        // one for the album to be worth calling resolved.
        Assert.All(catalog.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Isrc)));
        Assert.All(catalog.Tracks, t => Assert.NotNull(t.Duration));
        Assert.All(catalog.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.CoverArtUrl)));

        var ordered = catalog.Tracks.Select(t => t.TrackNumber).ToList();
        Assert.Equal(ordered.OrderBy(n => n), ordered);
    }

    [Fact]
    public async Task ReadsAWholeAppleMusicAlbum()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://music.apple.com/us/album/discovery/697194953"), enrich: false);

        Assert.NotNull(catalog);
        _output.WriteLine($"{catalog!.Title} - {catalog.Tracks.Count} tracks");

        Assert.Equal(14, catalog.Tracks.Count);
        Assert.All(catalog.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Genre)));
        Assert.All(catalog.Tracks, t => Assert.NotNull(t.Duration));
        Assert.Contains(catalog.Tracks, t => !string.IsNullOrWhiteSpace(t.Copyright));

        // Artwork must come back at the size that actually exists, not the 100px thumbnail.
        Assert.Contains("1400x1400", catalog.Tracks[0].CoverArtUrl ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgesASpotifyAlbumToACatalogueThatPublishesItsTracks()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://open.spotify.com/album/2noRn2Aes5aoNVsU6iWThc"), enrich: false);

        Assert.NotNull(catalog);
        _output.WriteLine($"{catalog!.Title} / {catalog.Artist} - bridged={catalog.WasBridged}");

        // Spotify publishes only a name and an artist anonymously; the track list has to come
        // from elsewhere, and the result must admit that it did.
        Assert.True(catalog.WasBridged);
        Assert.Equal(MusicPlatform.Deezer, catalog.BridgedVia);
        Assert.Equal(MusicPlatform.Spotify, catalog.Platform);
        Assert.True(catalog.Tracks.Count > 5);
        Assert.All(catalog.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Isrc)));
    }

    [Fact]
    public async Task AddsCanonicalIdentifiersFromMusicBrainz()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/track/3135556"), enrich: true);

        Assert.NotNull(catalog);
        var track = catalog!.Tracks[0];
        _output.WriteLine($"{track.Title} - ISRC {track.Isrc} - MBID {track.MusicBrainzRecordingId}");

        Assert.False(string.IsNullOrWhiteSpace(track.Isrc));
        Assert.False(string.IsNullOrWhiteSpace(track.MusicBrainzRecordingId));
    }

    [Fact]
    public async Task ReadsADeezerPlaylist()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/playlist/908622995"), enrich: false);

        Assert.NotNull(catalog);
        _output.WriteLine($"{catalog!.Title} - {catalog.Tracks.Count} of {catalog.DeclaredCount}");

        Assert.Equal(CatalogKind.Playlist, catalog.Kind);
        Assert.True(catalog.Tracks.Count > 10);
        Assert.All(catalog.Tracks, t => Assert.False(string.IsNullOrWhiteSpace(t.Title)));
    }

    [Fact]
    public async Task FindsCatalogueRecordingsWhereTheyCanActuallyBeFetched()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/album/302127"), enrich: false);
        Assert.NotNull(catalog);

        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);

        // The test host runs from its own output folder, which has no bundled tools beside it, so
        // the build cache is named explicitly rather than left to the usual search order.
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);
        Assert.True(tools.YtDlp.IsAvailable, "yt-dlp must be present for this test");

        var matcher = new TrackMatcher(runner, new LosslessSearcher(http, NullLogger<LosslessSearcher>.Instance), NullLogger<TrackMatcher>.Instance);

        // Three tracks is enough to show the scoring works without spending a search on all
        // fourteen; each one is a couple of network round trips.
        var confident = 0;
        foreach (var track in catalog!.Tracks.Take(3))
        {
            var matches = await matcher.FindAsync(
                track, tools, settings);

            var best = matches.FirstOrDefault();
            _output.WriteLine(
                $"{track.Title} ({track.Duration?.TotalSeconds:F0}s) -> " +
                (best is null ? "nothing" : $"[{best.Score}] {best.Title} ({best.Duration?.TotalSeconds:F0}s) :: {best.Reasoning}"));

            Assert.NotEmpty(matches);
            if (best!.IsConfident) confident++;

            // Whatever wins, it must be the right length: that is the check that stops a preview
            // or an edit being queued as the recording.
            Assert.NotNull(best.DurationDelta);
            Assert.True(Math.Abs(best.DurationDelta!.Value.TotalSeconds) <= 12,
                $"{best.Title} is {best.DurationDelta.Value.TotalSeconds:F0}s off");
        }

        Assert.True(confident >= 2, $"only {confident} of 3 matched confidently");
    }

    [Fact]
    public async Task ReportsWhereAWholeAlbumWouldActuallyComeFrom()
    {
        // Two questions this answers with evidence rather than assertion: which sources really
        // win, and whether anything short can slip through. Both matter -- a truncated upload
        // that passed as the recording would be the worst possible outcome here.
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/album/302127"), enrich: false);
        Assert.NotNull(catalog);

        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);
        Assert.True(tools.YtDlp.IsAvailable);

        var matcher = new TrackMatcher(runner, new LosslessSearcher(http, NullLogger<LosslessSearcher>.Instance), NullLogger<TrackMatcher>.Instance);
        var bySource = new Dictionary<string, int>(StringComparer.Ordinal);
        var worstDelta = 0.0;
        var unmatched = 0;

        foreach (var track in catalog!.Tracks)
        {
            var best = (await matcher.FindAsync(track, tools, settings)).FirstOrDefault();
            if (best is null)
            {
                unmatched++;
                _output.WriteLine($"{track.TrackNumber,2}. {track.Title,-42} NOTHING FOUND");
                continue;
            }

            var host = new Uri(best.Url).Host.Replace("www.", string.Empty, StringComparison.Ordinal);
            bySource[host] = bySource.GetValueOrDefault(host) + 1;

            var delta = best.DurationDelta?.TotalSeconds ?? 0;
            worstDelta = Math.Max(worstDelta, Math.Abs(delta));

            _output.WriteLine(
                $"{track.TrackNumber,2}. {track.Title,-42} {track.Duration?.TotalSeconds,4:F0}s " +
                $"-> {host,-16} {best.Duration?.TotalSeconds,4:F0}s ({delta,+5:F0}s) score {best.Score}");
        }

        _output.WriteLine("");
        foreach (var (host, count) in bySource.OrderByDescending(p => p.Value))
            _output.WriteLine($"{host}: {count}");
        _output.WriteLine($"unmatched: {unmatched}, worst length difference: {worstDelta:F0}s");

        // No winner may be short. This is the guarantee that matters: a preview or a clipped
        // upload must never be queued as the recording.
        Assert.True(worstDelta <= 12, $"a match was {worstDelta:F0}s off the stated length");
    }

    [Fact]
    public async Task PrefersALosslessSourceOverAStreamWhenOneExists()
    {
        // Ben Prunty prices this release at zero, so Bandcamp serves it as FLAC, WAV, ALAC and
        // AIFF rather than as the 128 kbit/s stream a paid release gets. The recording is also on
        // YouTube, lossily, so this is exactly the choice the ordering exists to make.
        using var http = NewClient();
        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);
        Assert.True(tools.YtDlp.IsAvailable);

        var matcher = new TrackMatcher(
            runner, new LosslessSearcher(http, NullLogger<LosslessSearcher>.Instance),
            NullLogger<TrackMatcher>.Instance);

        var track = new CatalogTrack
        {
            Title = "Lanius (Battle)",
            Artist = "Ben Prunty",
            Duration = TimeSpan.FromSeconds(261),
        };

        var best = (await matcher.FindAsync(track, tools, settings)).FirstOrDefault();

        Assert.NotNull(best);
        _output.WriteLine($"{best!.Source}: {best.AudioDescription} (lossless={best.IsLossless}) score {best.Score}");

        Assert.True(best.IsLossless, $"expected a lossless source, got {best.Source} {best.AudioDescription}");
        Assert.Contains("FLAC", best.AudioDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACommercialRecordingStillFallsBackToTheStream()
    {
        // The other half of the same rule. No free lossless source carries major-label catalogue,
        // and the search must not stall or refuse when it finds none -- it falls back and says
        // plainly that what it found is lossy.
        using var http = NewClient();
        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);

        var matcher = new TrackMatcher(
            runner, new LosslessSearcher(http, NullLogger<LosslessSearcher>.Instance),
            NullLogger<TrackMatcher>.Instance);

        var track = new CatalogTrack
        {
            Title = "Harder, Better, Faster, Stronger",
            Artist = "Daft Punk",
            Duration = TimeSpan.FromSeconds(226),
        };

        var best = (await matcher.FindAsync(track, tools, settings)).FirstOrDefault();

        Assert.NotNull(best);
        _output.WriteLine($"{best!.Source}: lossless={best.IsLossless} score {best.Score}");

        Assert.True(best.IsConfident);
        Assert.NotNull(best.DurationDelta);
        Assert.True(Math.Abs(best.DurationDelta!.Value.TotalSeconds) <= 12);
    }

    [Fact]
    public async Task AWholeAlbumIsMatchedInAFractionOfTheSequentialTime()
    {
        // Sequential, in full, before any download: fourteen tracks took nearly three minutes.
        // The pipeline exists so the first file starts in seconds instead, and this measures both
        // the total and how long the first result took.
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/album/302127"), enrich: false);
        Assert.NotNull(catalog);

        var runner = new MiguelDownloader.Engine.Processes.ProcessRunner(
            NullLogger<MiguelDownloader.Engine.Processes.ProcessRunner>.Instance);
        var locator = new MiguelDownloader.Engine.Dependencies.ToolLocator(
            runner, NullLogger<MiguelDownloader.Engine.Dependencies.ToolLocator>.Instance);
        var settings = BuildToolSettings();
        var tools = await locator.ResolveAsync(settings);
        Assert.True(tools.YtDlp.IsAvailable);

        var session = new CatalogMatchSession(
            new TrackMatcher(runner,
                new LosslessSearcher(http, NullLogger<LosslessSearcher>.Instance),
                NullLogger<TrackMatcher>.Instance),
            NullLogger<CatalogMatchSession>.Instance);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? firstResult = null;
        var found = 0;
        var total = 0;

        await foreach (var result in session.MatchAllAsync(catalog!, tools, settings))
        {
            firstResult ??= clock.Elapsed;
            total++;
            if (result.Found) found++;
        }
        clock.Stop();

        _output.WriteLine($"first result after {firstResult?.TotalSeconds:F1}s");
        _output.WriteLine($"all {total} matched in {clock.Elapsed.TotalSeconds:F1}s ({found} found)");

        Assert.Equal(catalog!.Tracks.Count, total);
        Assert.True(found >= catalog.Tracks.Count - 1, $"only {found} of {total} were found");

        // The sequential run took 176 seconds. Anything near that means the pipeline is not
        // working; the threshold is deliberately loose so a slow network does not fail the build.
        Assert.True(clock.Elapsed.TotalSeconds < 110,
            $"matching took {clock.Elapsed.TotalSeconds:F0}s, no better than sequential");

        // The point of the pipeline: something to download almost immediately.
        Assert.True(firstResult!.Value.TotalSeconds < 40,
            $"the first result took {firstResult.Value.TotalSeconds:F0}s");
    }

    [Fact]
    public async Task ANonsenseLinkResolvesToNothingRatherThanToSomethingWrong()
    {
        using var http = NewClient();
        var catalog = await BuildService(http)
            .ResolveAsync(Parse("https://www.deezer.com/album/000000000000"), enrich: false);

        Assert.Null(catalog);
    }
}

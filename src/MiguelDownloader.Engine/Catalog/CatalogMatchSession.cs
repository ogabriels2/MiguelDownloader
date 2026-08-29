using System.Threading.Channels;
using MiguelDownloader.Core.Music;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Engine.Dependencies;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Engine.Catalog;

/// <summary>One catalogue track and where it was found, or that it was not.</summary>
public sealed record MatchedTrack(CatalogTrack Track, TrackMatch? Match, int Index)
{
    public bool Found => Match is not null;
}

/// <summary>
/// Matches a whole catalogue, arranged so the first download starts in seconds rather than
/// minutes.
/// <para>
/// Matching is network waiting, not work: a single track spends around twelve seconds asking four
/// services questions and almost none of it computing. Done one at a time and in full before any
/// download begins, a fourteen-track album took nearly three minutes before a single byte was
/// fetched, and a fifty-track playlist would take ten.
/// </para>
/// <para>
/// Four things fix that, and none of them changes what is accepted -- only when the search stops
/// looking. The duration tolerance, the preference for lossless and the confidence threshold are
/// exactly as they were.
/// </para>
/// </summary>
public sealed class CatalogMatchSession(TrackMatcher matcher, ILogger<CatalogMatchSession> logger)
{
    /// <summary>
    /// How many tracks are matched at once.
    /// <para>
    /// Four, because this is latency and not load: the services are being waited on, not worked.
    /// Higher starts to look like scraping to the sites involved, which is neither polite nor
    /// reliable.
    /// </para>
    /// </summary>
    public const int Concurrency = 4;

    /// <summary>
    /// Consecutive misses before a whole album stops being searched for losslessly.
    /// <para>
    /// An album is one release on one label. If its first few tracks are not on Bandcamp or the
    /// Archive, the rest are not either, and asking eleven more times costs a minute to learn
    /// what three requests already established.
    /// </para>
    /// </summary>
    public const int AlbumMissLimit = 3;

    /// <summary>
    /// The same for a playlist, which mixes artists and labels, so a miss says much less about
    /// the next track. High enough to be near-certain, low enough to stop a long mainstream
    /// playlist spending minutes on searches that will not find anything.
    /// </summary>
    public const int PlaylistMissLimit = 10;

    private readonly TrackMatcher _matcher = matcher;
    private readonly ILogger<CatalogMatchSession> _logger = logger;

    /// <summary>
    /// Matches every track, yielding each as soon as it resolves so the caller can queue it while
    /// the rest are still being looked for.
    /// <para>
    /// Results arrive in completion order, not catalogue order; each carries its index so the
    /// caller can restore the running order when it matters.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<MatchedTrack> MatchAllAsync(
        MusicCatalog catalog,
        ToolPaths tools,
        AdvancedSettings advanced,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var missLimit = catalog.Kind == CatalogKind.Album ? AlbumMissLimit : PlaylistMissLimit;
        var prospects = new LosslessProspects(missLimit);

        // Small batches rather than one enormous invocation: the whole album in a single call
        // would be marginally faster overall but would deliver nothing until it finished, and the
        // point of this is that the first download starts quickly.
        var start = 0;
        var isFirstBatch = true;

        while (start < catalog.Tracks.Count)
        {
            // The first batch is deliberately small. Nothing can be handed over until a batch
            // finishes, so a small one gets the first download started sooner -- and it settles
            // the question of whether this release exists losslessly before the larger batches
            // spend time asking again.
            var size = isFirstBatch ? FirstBatchSize : BatchSize;
            var batch = catalog.Tracks.Skip(start).Take(size).ToList();
            var offset = start;
            start += batch.Count;
            isFirstBatch = false;

            // Both halves start together. The lossless lookups are HTTP and the fallback search
            // is a yt-dlp invocation; waiting for one before beginning the other would add the
            // two costs together for no reason.
            var losslessTask = prospects.StillWorthAsking
                ? LookUpLosslessAsync(batch, tools, advanced, cancellationToken)
                : Task.FromResult(new Dictionary<string, TrackMatch>(StringComparer.Ordinal));

            var fallbackTask = _matcher.SearchManyAsync(batch, tools, advanced, cancellationToken);

            await Task.WhenAll(losslessTask, fallbackTask).ConfigureAwait(false);

            var losslessLookups = losslessTask.Result;
            var fallback = fallbackTask.Result;

            for (var i = 0; i < batch.Count; i++)
            {
                var track = batch[i];
                losslessLookups.TryGetValue(track.SearchQuery, out var lossless);
                fallback.TryGetValue(track.SearchQuery, out var lossy);

                var best = ChooseBest(lossless, lossy);
                if (prospects.StillWorthAsking) prospects.Record(best is { IsLossless: true });

                yield return new MatchedTrack(track, best, offset + i);
            }
        }
    }

    /// <summary>
    /// How many tracks share one search invocation. Five amortises the tool's start-up -- five to
    /// six seconds whether it answers one query or ten -- without making a batch so large that
    /// nothing is handed over for a long time.
    /// </summary>
    public const int BatchSize = 5;

    /// <summary>
    /// The first batch is smaller, because nothing can start downloading until a batch completes
    /// and the wait before the first file is what the user actually feels.
    /// </summary>
    public const int FirstBatchSize = 2;

    /// <summary>
    /// Picks between a lossless candidate and a lossy one.
    /// <para>
    /// Lossless wins when it is a confident match. It does not win merely by being lossless: the
    /// wrong recording in FLAC is still the wrong recording, and a doubtful lossless hit loses to
    /// a confident stream.
    /// </para>
    /// </summary>
    private static TrackMatch? ChooseBest(TrackMatch? lossless, List<TrackMatch>? lossy)
    {
        var bestLossy = lossy?.FirstOrDefault();
        if (lossless is null) return bestLossy;
        if (bestLossy is null) return lossless;

        return lossless.IsConfident ? lossless : bestLossy;
    }

    private async Task<Dictionary<string, TrackMatch>> LookUpLosslessAsync(
        IReadOnlyList<CatalogTrack> batch, ToolPaths tools, AdvancedSettings advanced,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, TrackMatch>(StringComparer.Ordinal);

        using var gate = new SemaphoreSlim(Concurrency);
        var lookups = batch.Select(async track =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var found = await _matcher
                    .FindLosslessOnlyAsync(track, tools, advanced, cancellationToken)
                    .ConfigureAwait(false);
                return (track.SearchQuery, Match: found.FirstOrDefault());
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        foreach (var (query, match) in await Task.WhenAll(lookups).ConfigureAwait(false))
        {
            if (match is not null) results[query] = match;
        }
        return results;
    }

    /// <summary>
    /// Remembers whether the lossless sources are still worth asking for this release.
    /// <para>
    /// A single hit resets the count: a release that is on Bandcamp is on Bandcamp for all of its
    /// tracks, and one miss in the middle is a naming difference rather than evidence that the
    /// release is absent.
    /// </para>
    /// </summary>
    private sealed class LosslessProspects(int missLimit)
    {
        // System.Threading.Lock is .NET 9; this project targets .NET 8.
        private readonly object _lock = new();
        private int _consecutiveMisses;
        private bool _giveUp;

        public bool StillWorthAsking
        {
            get { lock (_lock) return !_giveUp; }
        }

        public void Record(bool found)
        {
            lock (_lock)
            {
                if (found) { _consecutiveMisses = 0; return; }
                if (++_consecutiveMisses >= missLimit) _giveUp = true;
            }
        }
    }
}

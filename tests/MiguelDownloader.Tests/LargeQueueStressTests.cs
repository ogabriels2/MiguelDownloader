using System.Diagnostics;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// Exercises the queue and its storage at the sizes a real music library reaches.
/// <para>
/// A playlist of a thousand tracks is an ordinary request, not an edge case, and "it should
/// scale" is not a claim worth making without measuring. These tests put real numbers on
/// enqueueing, persisting, restoring and querying, and fail if any of them degrades past a
/// bound that would be felt as a freeze.
/// </para>
/// <para>
/// Run with <c>dotnet test --filter Category=Stress</c>. Excluded from the default run because
/// the largest case writes tens of thousands of rows.
/// </para>
/// </summary>
[Trait("Category", "Stress")]
public sealed class LargeQueueStressTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly AppDatabase _database;

    public LargeQueueStressTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "md-stress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _database = new AppDatabase(Path.Combine(_root, "stress.db"), NullLogger<AppDatabase>.Instance);
        _database.Initialize();
    }

    private static DownloadTask MakeTask(int index, string directory)
    {
        var item = new MediaItem
        {
            Id = $"vid{index:D6}",
            // Titles carry characters that have to survive naming and storage.
            Title = $"Faixa {index:D4} — Ação & Coração \"quoted\" (ao vivo)",
            WebpageUrl = $"https://www.youtube.com/watch?v=vid{index:D6}",
            ChannelName = $"Artista {index % 50}",
            Duration = TimeSpan.FromSeconds(120 + (index % 300)),
            Kind = MediaKind.Music,
            Thumbnails = [new ThumbnailInfo { Url = $"https://i.ytimg.com/vi/vid{index:D6}/hq.jpg" }],
            Music = new MusicMetadata
            {
                Title = $"Faixa {index:D4}",
                Artist = $"Artista {index % 50}",
                Album = $"Album {index % 80}",
                TrackNumber = (index % 20) + 1,
            },
        };

        return new DownloadTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Order = index,
            Request = new DownloadRequest
            {
                Url = item.WebpageUrl,
                Item = item,
                Mode = DownloadMode.Audio,
                Selection = new FormatSelection { Priority = FormatPriority.Quality },
                AudioFormat = AudioOutputFormat.KeepOriginal,
                TargetDirectory = directory,
                TargetFileName = $"{index:D4} - Faixa.m4a",
                Music = item.Music,
                CollectionId = "PLstress",
                CollectionTitle = "Playlist de estresse",
                IndexInCollection = index,
            },
        };
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    public async Task PersistsAndRestoresALargeQueueQuickly(int count)
    {
        var repository = new QueueRepository(_database, NullLogger<QueueRepository>.Instance);
        var tasks = Enumerable.Range(1, count).Select(i => MakeTask(i, _root)).ToList();

        var save = Stopwatch.StartNew();
        await repository.SaveManyAsync(tasks);
        save.Stop();

        var load = Stopwatch.StartNew();
        var restored = await repository.LoadAsync();
        load.Stop();

        _output.WriteLine($"{count,6} items | save {save.ElapsedMilliseconds,6} ms | load {load.ElapsedMilliseconds,6} ms");

        Assert.Equal(count, restored.Count);

        // Order has to survive the round trip: an album restored out of sequence is wrong.
        Assert.Equal(tasks.Select(t => t.Order), restored.Select(t => t.Order));

        // Bounds sized so a regression that makes this feel like a freeze fails the test,
        // while leaving generous headroom for a slow machine.
        Assert.True(save.ElapsedMilliseconds < 30_000,
            $"saving {count} items took {save.ElapsedMilliseconds} ms");
        Assert.True(load.ElapsedMilliseconds < 15_000,
            $"loading {count} items took {load.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task RestoredItemsCarryTheirIdentityWithoutTheFormatList()
    {
        var repository = new QueueRepository(_database, NullLogger<QueueRepository>.Instance);
        var tasks = Enumerable.Range(1, 200).Select(i => MakeTask(i, _root)).ToList();

        await repository.SaveManyAsync(tasks);
        var restored = await repository.LoadAsync();

        var first = restored.First();

        Assert.Equal("Faixa 0001 — Ação & Coração \"quoted\" (ao vivo)", first.Request.Item.Title);
        Assert.Equal("Artista 1", first.Request.Item.ChannelName);
        Assert.Equal("Album 1", first.Request.Music?.Album);

        // Formats are deliberately not stored: they expire within hours and are re-fetched.
        Assert.Empty(first.Request.Item.Formats);
        Assert.True(first.Request.Item.IsStub);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    public async Task HistorySearchStaysFastWithManyRows(int count)
    {
        var history = new HistoryRepository(_database);

        var insert = Stopwatch.StartNew();
        await history.AddManyAsync(Enumerable.Range(1, count).Select(i => new HistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceId = $"vid{i:D6}",
            Url = $"https://www.youtube.com/watch?v=vid{i:D6}",
            Title = $"Faixa {i:D4} — Ação & Coração",
            Author = $"Artista {i % 50}",
            Kind = MediaKind.Music,
            Mode = DownloadMode.Audio,
            FileSize = 3_000_000 + i,
            Duration = TimeSpan.FromSeconds(180),
            Status = DownloadStatus.Completed,
            CompletedAt = DateTimeOffset.Now.AddMinutes(-i),
        }).ToList());
        insert.Stop();

        var search = Stopwatch.StartNew();
        var results = await history.QueryAsync(new HistoryQuery { SearchText = "Artista 7", Limit = 500 });
        search.Stop();

        var sort = Stopwatch.StartNew();
        var largest = await history.QueryAsync(new HistoryQuery { Sort = HistorySort.LargestFirst, Limit = 500 });
        sort.Stop();

        _output.WriteLine(
            $"{count,6} rows | insert {insert.ElapsedMilliseconds,6} ms | " +
            $"search {search.ElapsedMilliseconds,5} ms ({results.Count} hits) | " +
            $"sort {sort.ElapsedMilliseconds,5} ms");

        Assert.NotEmpty(results);
        Assert.NotEmpty(largest);

        // Searching is interactive: it runs on every keystroke, so it has to stay snappy.
        Assert.True(search.ElapsedMilliseconds < 2_000, $"search took {search.ElapsedMilliseconds} ms");
        Assert.True(sort.ElapsedMilliseconds < 2_000, $"sort took {sort.ElapsedMilliseconds} ms");
        Assert.True(insert.ElapsedMilliseconds < 30_000, $"insert took {insert.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    public async Task EnqueueingManyItemsIsLinear(int count)
    {
        // The queue must not re-scan itself per item. An O(n^2) enqueue is invisible at ten
        // items and locks the interface at five thousand.
        var executor = StressHarness.CreateExecutor();
        await using var queue = new MiguelDownloader.Engine.Downloads.DownloadQueue(
            executor, NullLogger<MiguelDownloader.Engine.Downloads.DownloadQueue>.Instance);

        // No tools configured, so nothing actually starts; this measures bookkeeping only.
        queue.Configure(() => new AppSettings(), () => null);

        var tasks = Enumerable.Range(1, count).Select(i => MakeTask(i, _root)).ToList();

        var enqueue = Stopwatch.StartNew();
        queue.EnqueueRange(tasks);
        enqueue.Stop();

        var snapshot = Stopwatch.StartNew();
        var listed = queue.Tasks;
        snapshot.Stop();

        _output.WriteLine(
            $"{count,6} items | enqueue {enqueue.ElapsedMilliseconds,5} ms | " +
            $"snapshot {snapshot.ElapsedMilliseconds,5} ms | pending {queue.PendingCount}");

        Assert.Equal(count, listed.Count);
        Assert.Equal(count, queue.PendingCount);

        Assert.True(enqueue.ElapsedMilliseconds < 5_000,
            $"enqueueing {count} items took {enqueue.ElapsedMilliseconds} ms");
        Assert.True(snapshot.ElapsedMilliseconds < 2_000,
            $"listing {count} items took {snapshot.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task MemoryPerQueuedItemStaysReasonable()
    {
        const int count = 5_000;
        var executor = StressHarness.CreateExecutor();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalMemory(true);

        await using var queue = new MiguelDownloader.Engine.Downloads.DownloadQueue(
            executor, NullLogger<MiguelDownloader.Engine.Downloads.DownloadQueue>.Instance);
        queue.Configure(() => new AppSettings(), () => null);
        queue.EnqueueRange(Enumerable.Range(1, count).Select(i => MakeTask(i, _root)).ToList());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var after = GC.GetTotalMemory(true);

        var perItem = (after - before) / (double)count;
        _output.WriteLine($"{count} queued: {(after - before) / 1024.0 / 1024.0:F1} MB total, {perItem:F0} bytes/item");

        // Well clear of what a queued item needs; a regression that starts retaining the format
        // list or a bitmap per row would blow straight through this.
        Assert.True(perItem < 8_000, $"{perItem:F0} bytes per queued item is more than expected");
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* best effort */ }
    }
}

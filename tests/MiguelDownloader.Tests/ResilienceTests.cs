using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Naming;
using MiguelDownloader.Core.Settings;
using MiguelDownloader.Data;
using MiguelDownloader.Engine.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MiguelDownloader.Tests;

/// <summary>
/// Deliberately hostile conditions: full disks, vanished folders, unwritable destinations,
/// corrupt state files and names Windows refuses.
/// <para>
/// These are the situations that decide whether the application degrades or loses the user's
/// work, and none of them can be verified by reasoning about the code alone.
/// </para>
/// </summary>
public sealed class ResilienceTests(ITestOutputHelper output) : IDisposable
{
    private readonly ITestOutputHelper _output = output;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "md-resilience", Guid.NewGuid().ToString("N"));

    private string Scratch
    {
        get
        {
            Directory.CreateDirectory(_root);
            return _root;
        }
    }

    // ---------------------------------------------------------------- disk and destinations

    [Fact]
    public void PredictsWhenADownloadCannotFit()
    {
        // A size larger than any real volume must be refused before a byte is transferred.
        var check = DiskSpace.Check(Scratch, long.MaxValue / 4);

        Assert.False(check.IsSufficient);
        Assert.True(check.RequiredBytes > 0);
    }

    [Fact]
    public void AnUnknownSizeDoesNotBlockTheDownload()
    {
        // Refusing over a number we do not have would be worse than letting it run and failing
        // with a real error if it genuinely does not fit.
        Assert.True(DiskSpace.Check(Scratch, null).IsSufficient);
    }

    [Fact]
    public void AVanishedDriveIsReportedAsUnknownRatherThanCrashing()
    {
        // A mapped drive that went away mid-session must not throw out of the space check.
        var check = DiskSpace.Check(@"Z:\gone\missing", 1_000_000);

        Assert.True(check.IsUnknown || check.IsSufficient);
    }

    [Fact]
    public void WritingToAProtectedLocationIsClassifiedClearly()
    {
        var error = ErrorClassifier.Classify(new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Equal(DownloadErrorKind.PermissionDenied, error.Kind);
        Assert.Equal(RecommendedAction.ChooseAnotherFolder, error.Action);
    }

    [Fact]
    public void ADestinationThatDoesNotExistYetIsCreated()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var target = Path.Combine(Scratch, "nova", "subpasta", "mais fundo");

        var plan = workspace.PlanDestination(target, "video.mp4", ExistingFilePolicy.Ask);

        Assert.True(Directory.Exists(target));
        Assert.Equal(CollisionOutcome.NoCollision, plan.Outcome);
    }

    // ---------------------------------------------------------------- names Windows refuses

    [Fact]
    public void AnAbsurdlyLongTitleStillProducesAUsablePath()
    {
        // Real titles run long; a 700-character one must not produce an unopenable path.
        var title = string.Concat(Enumerable.Repeat("Título muito longo com acentuação ", 25));
        var directory = Path.Combine(Scratch, "destino");

        var name = FileNameSanitizer.SanitizeComponent(title) + ".mkv";
        var fitted = FileNameSanitizer.EnsurePathFits(directory, name);
        var full = Path.Combine(directory, fitted);

        _output.WriteLine($"{title.Length} chars -> {full.Length} char path");

        Assert.True(full.Length <= 240);
        Assert.EndsWith(".mkv", fitted, StringComparison.Ordinal);

        // The decisive check: Windows actually accepts it.
        Directory.CreateDirectory(directory);
        File.WriteAllText(full, "ok");
        Assert.True(File.Exists(full));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("LPT1.mp4")]
    [InlineData("AC/DC: Live \"Best\" <2024> | 100%?*")]
    [InlineData("trailing dot.")]
    [InlineData("  espaços  ")]
    public void HostileTitlesProduceFilesWindowsWillActuallyCreate(string title)
    {
        var directory = Path.Combine(Scratch, "hostis");
        Directory.CreateDirectory(directory);

        var name = FileNameSanitizer.SanitizeComponent(title) + ".m4a";
        var full = Path.Combine(directory, FileNameSanitizer.EnsurePathFits(directory, name));

        File.WriteAllText(full, "ok");
        Assert.True(File.Exists(full), $"Windows refused the name produced for {title}");
    }

    // ---------------------------------------------------------------- partial files

    [Fact]
    public void AnExistingPartialFileIsNotMistakenForTheResult()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var work = Path.Combine(Scratch, "trabalho");
        Directory.CreateDirectory(work);

        // What an interrupted download leaves behind.
        File.WriteAllBytes(Path.Combine(work, "video.mp4.part"), new byte[5_000_000]);
        File.WriteAllText(Path.Combine(work, "video.mp4.ytdl"), "{}");

        Assert.Null(workspace.FindProducedMedia(work));

        // Once the real file lands it is found, even though the larger .part is still there.
        File.WriteAllBytes(Path.Combine(work, "video.mp4"), new byte[2_000_000]);
        var found = workspace.FindProducedMedia(work);

        Assert.NotNull(found);
        Assert.EndsWith(".mp4", found, StringComparison.Ordinal);
        Assert.DoesNotContain(".part", found!, StringComparison.Ordinal);
    }

    [Fact]
    public void SidecarsAreNeverMistakenForTheMedia()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var work = Path.Combine(Scratch, "sidecars");
        Directory.CreateDirectory(work);

        File.WriteAllText(Path.Combine(work, "video.pt.srt"), "1\n00:00:01,000 --> 00:00:02,000\noi\n");
        File.WriteAllBytes(Path.Combine(work, "video.jpg"), new byte[80_000]);
        File.WriteAllText(Path.Combine(work, "video.info.json"), "{}");

        Assert.Null(workspace.FindProducedMedia(work));
        Assert.Single(workspace.FindSubtitleFiles(work));
        Assert.Single(workspace.FindImageFiles(work));
    }

    [Fact]
    public void OrphanedWorkspacesAreSweptButLiveOnesSurvive()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var settings = new DownloadSettings { TemporaryFolder = Path.Combine(Scratch, "work-root") };

        var live = workspace.CreateWorkspace("task-alive", settings);
        var orphan = workspace.CreateWorkspace("task-orphan", settings);
        File.WriteAllBytes(Path.Combine(orphan, "leftover.part"), new byte[1000]);

        var removed = workspace.SweepOrphans(new HashSet<string> { "task-alive" }, settings);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(live), "a live job's workspace was deleted");
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void CustomTemporaryFolderNeverMakesUnrelatedDirectoriesApplicationOwned()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var selected = Path.Combine(Scratch, "custom-temporary-folder");
        var unrelated = Path.Combine(selected, "family-photos");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "not owned by the downloader");

        var settings = new DownloadSettings { TemporaryFolder = selected };
        var orphan = workspace.CreateWorkspace("task-orphan", settings);
        var removed = workspace.SweepOrphans(new HashSet<string>(), settings);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal(
            Path.Combine(selected, "MiguelDownloader-work"),
            WorkspaceManager.ResolveRoot(settings));
    }

    [Fact]
    public void RecursiveCleanupRefusesAnUnmarkedDirectory()
    {
        var workspace = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        var unrelated = Path.Combine(Scratch, "unmarked");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "important");

        workspace.CleanWorkspace(unrelated);

        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
    }

    // ---------------------------------------------------------------- corrupt state

    [Fact]
    public void ACorruptSettingsFileIsQuarantinedAndDefaultsLoad()
    {
        var path = Path.Combine(Scratch, "settings.json");
        File.WriteAllText(path, "{ this is not valid json ");

        var store = new SettingsStore(path, NullLogger<SettingsStore>.Instance);
        var settings = store.Load();

        // The application still opens, with defaults.
        Assert.NotNull(settings);
        Assert.False(string.IsNullOrWhiteSpace(settings.General.DownloadFolder));

        // And the unreadable file is moved aside rather than destroyed.
        var quarantined = Directory.GetFiles(Scratch, "settings.json.corrupt-*");
        Assert.NotEmpty(quarantined);
        _output.WriteLine($"quarantined as {Path.GetFileName(quarantined[0])}");
    }

    [Fact]
    public async Task OneUnreadableQueueRowDoesNotLoseTheRest()
    {
        var database = new AppDatabase(Path.Combine(Scratch, "queue.db"), NullLogger<AppDatabase>.Instance);
        database.Initialize();

        var repository = new QueueRepository(database, NullLogger<QueueRepository>.Instance);
        await repository.SaveManyAsync([MakeTask("good-1"), MakeTask("good-2")]);

        // Corrupt one row the way a partial write or a schema change would.
        await using (var connection = database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO queue (id, ordinal, status, stage, created_at, attempt_count, request_json) " +
                "VALUES ('broken', 99, 0, 0, @now, 0, '{ not json ');";
            command.Parameters.AddWithValue("@now", DateTimeOffset.Now.ToString("O"));
            command.ExecuteNonQuery();
        }

        var restored = await repository.LoadAsync();

        // The damaged row is skipped; the good ones still come back.
        Assert.Equal(2, restored.Count);
        Assert.DoesNotContain(restored, t => t.Id == "broken");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void MigrationsAreIdempotent()
    {
        // Opening an already-current database must not try to re-apply anything.
        var path = Path.Combine(Scratch, "migrate.db");
        var first = new AppDatabase(path, NullLogger<AppDatabase>.Instance);
        first.Initialize();
        first.Initialize();

        var second = new AppDatabase(path, NullLogger<AppDatabase>.Instance);
        second.Initialize();

        using var connection = second.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM queue;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ---------------------------------------------------------------- collections

    [Fact]
    public void OneBadItemDoesNotInvalidateAWholePlaylist()
    {
        // 1.247 items where a handful are unavailable is a valid outcome, not a failed playlist.
        var items = Enumerable.Range(1, 20).Select(i => new MediaItem
        {
            Id = $"v{i:D3}",
            Title = $"Item {i}",
            WebpageUrl = $"https://www.youtube.com/watch?v=v{i:D3}",
            // Some entries arrive with nothing but an id: no duration, no thumbnail, no author.
            Duration = i % 5 == 0 ? null : TimeSpan.FromMinutes(3),
            Thumbnails = i % 4 == 0 ? [] : [new ThumbnailInfo { Url = "https://i.ytimg.com/x.jpg" }],
            ChannelName = i % 3 == 0 ? null : "Canal",
            IsStub = true,
        }).ToList();

        var collection = new MediaCollection
        {
            Id = "PL1", Title = "Mista", WebpageUrl = "https://www.youtube.com/playlist?list=PL1",
            Items = items, DeclaredCount = 25,
        };

        Assert.Equal(20, collection.Count);
        Assert.True(collection.IsTruncated, "a listing short of its declared count is truncated");

        // Items missing metadata must not throw when the interface reads them.
        foreach (var item in collection.Items)
        {
            _ = item.DisplayAuthor;
            _ = item.BestThumbnail;
            _ = item.Duration;
        }

        // A total duration is still computed from the items that do report one.
        Assert.NotNull(collection.TotalDuration);
    }

    [Fact]
    public void DuplicateTitlesInOnePlaylistGetDistinctFileNames()
    {
        // Two tracks with the same title must not race for the same path.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            var name = FileNameSanitizer.MakeUnique(@"C:\out", "Mesma Faixa.m4a", used.Contains);
            used.Add(Path.Combine(@"C:\out", name));
            names.Add(name);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ---------------------------------------------------------------- error surfaces

    [Theory]
    [InlineData("ERROR: unable to open for writing: [Errno 28] No space left on device", DownloadErrorKind.DiskFull)]
    [InlineData("ERROR: [Errno 13] Permission denied: 'C:\\\\Windows\\\\x.mp4'", DownloadErrorKind.PermissionDenied)]
    [InlineData("ERROR: ffmpeg exited with code 1\nUnknown encoder 'libx264'", DownloadErrorKind.ConversionFailed)]
    [InlineData("ERROR: Postprocessing: Error opening output file", DownloadErrorKind.MetadataFailed)]
    public void ToolFailuresBecomeActionableMessages(string stderr, DownloadErrorKind expected)
    {
        var error = ErrorClassifier.Classify(stderr);

        Assert.Equal(expected, error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.MessageKey));
        Assert.Equal(stderr, error.TechnicalDetails);
    }

    [Fact]
    public void APermanentFailureIsNotRetriedForever()
    {
        var removed = ErrorClassifier.Classify("ERROR: This video has been removed by the uploader");

        Assert.True(removed.IsPermanent);
        Assert.False(removed.IsRetryable);
    }

    private static DownloadTask MakeTask(string id) => new()
    {
        Id = id,
        Request = new DownloadRequest
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            Item = new MediaItem
            {
                Id = "dQw4w9WgXcQ",
                Title = "Teste",
                WebpageUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            },
            TargetDirectory = @"C:\out",
            TargetFileName = "teste.mp4",
            Selection = new FormatSelection(),
        },
    };

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

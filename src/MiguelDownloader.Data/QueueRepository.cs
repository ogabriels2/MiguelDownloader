using System.Text.Json;
using System.Text.Json.Serialization;
using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Formats;
using MiguelDownloader.Core.Models;
using MiguelDownloader.Core.Settings;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Data;

/// <summary>
/// The persisted shape of a queued job.
/// <para>
/// The full analysed item is deliberately not stored. Its format list runs to hundreds of
/// entries carrying signed URLs that expire within hours, so keeping it would bloat the database
/// with data that is useless by the time it is read back. Only the identity and display fields
/// are kept; the formats are re-fetched when the job resumes, which is also what makes a job
/// restored from a previous session pick up a still-valid stream.
/// </para>
/// </summary>
internal sealed record PersistedRequest
{
    public required string Url { get; init; }
    public required string ItemId { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public string? ThumbnailUrl { get; init; }
    public double? DurationSeconds { get; init; }
    public MediaKind Kind { get; init; }

    public DownloadMode Mode { get; init; }
    public FormatSelection Selection { get; init; } = new();
    public ContainerFormat Container { get; init; }
    public AudioOutputFormat AudioFormat { get; init; }
    public int LossyQuality { get; init; }
    public SubtitleRequest Subtitles { get; init; } = SubtitleRequest.Disabled;

    public bool EmbedThumbnail { get; init; }
    public bool WriteThumbnailFile { get; init; }
    public bool EmbedMetadata { get; init; }
    public bool EmbedChapters { get; init; }

    public required string TargetDirectory { get; init; }
    public required string TargetFileName { get; init; }
    public ExistingFilePolicy ExistingFilePolicy { get; init; }

    public MusicMetadata? Music { get; init; }
    public string? CollectionId { get; init; }
    public string? CollectionTitle { get; init; }
    public int? IndexInCollection { get; init; }
}

/// <summary>Persists the queue so it survives a restart.</summary>
public sealed class QueueRepository(AppDatabase database, ILogger<QueueRepository> logger)
{
    private readonly AppDatabase _database = database;
    private readonly ILogger<QueueRepository> _logger = logger;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Inserts or updates one task.</summary>
    public async Task SaveAsync(DownloadTask task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR REPLACE INTO queue
                (id, ordinal, status, stage, created_at, started_at, attempt_count, working_dir, request_json)
            VALUES
                (@id, @ordinal, @status, @stage, @created_at, @started_at, @attempts, @working_dir, @request);
            """;

        command.Parameters.AddWithValue("@id", task.Id);
        command.Parameters.AddWithValue("@ordinal", task.Order);
        command.Parameters.AddWithValue("@status", (int)task.Status);
        command.Parameters.AddWithValue("@stage", (int)task.Stage);
        command.Parameters.AddWithValue("@created_at", task.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("@started_at",
            task.StartedAt is { } s ? s.ToString("O") : (object)DBNull.Value);
        command.Parameters.AddWithValue("@attempts", task.AttemptCount);
        command.Parameters.AddWithValue("@working_dir", (object?)task.WorkingDirectory ?? DBNull.Value);
        command.Parameters.AddWithValue("@request", Serialize(task.Request));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists many tasks in one transaction.
    /// <para>
    /// Queueing a thousand-track playlist through the single-task path would open a thousand
    /// connections and commit a thousand transactions, which takes long enough to feel like the
    /// application has hung. One transaction with a reused prepared statement turns that into a
    /// single write.
    /// </para>
    /// </summary>
    public async Task SaveManyAsync(
        IReadOnlyCollection<DownloadTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (tasks.Count == 0) return;

        await using var connection = _database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO queue
                (id, ordinal, status, stage, created_at, started_at, attempt_count, working_dir, request_json)
            VALUES
                (@id, @ordinal, @status, @stage, @created_at, @started_at, @attempts, @working_dir, @request);
            """;

        // Parameters are created once and their values swapped per row.
        var id = command.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
        var ordinal = command.Parameters.Add("@ordinal", Microsoft.Data.Sqlite.SqliteType.Integer);
        var status = command.Parameters.Add("@status", Microsoft.Data.Sqlite.SqliteType.Integer);
        var stage = command.Parameters.Add("@stage", Microsoft.Data.Sqlite.SqliteType.Integer);
        var createdAt = command.Parameters.Add("@created_at", Microsoft.Data.Sqlite.SqliteType.Text);
        var startedAt = command.Parameters.Add("@started_at", Microsoft.Data.Sqlite.SqliteType.Text);
        var attempts = command.Parameters.Add("@attempts", Microsoft.Data.Sqlite.SqliteType.Integer);
        var workingDir = command.Parameters.Add("@working_dir", Microsoft.Data.Sqlite.SqliteType.Text);
        var request = command.Parameters.Add("@request", Microsoft.Data.Sqlite.SqliteType.Text);

        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            id.Value = task.Id;
            ordinal.Value = task.Order;
            status.Value = (int)task.Status;
            stage.Value = (int)task.Stage;
            createdAt.Value = task.CreatedAt.ToString("O");
            startedAt.Value = task.StartedAt is { } s ? s.ToString("O") : DBNull.Value;
            attempts.Value = task.AttemptCount;
            workingDir.Value = (object?)task.WorkingDirectory ?? DBNull.Value;
            request.Value = Serialize(task.Request);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Persisted {Count} queued tasks in one transaction", tasks.Count);
    }

    /// <summary>Removes a task from the persisted queue.</summary>
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queue WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears everything that has already finished.</summary>
    public async Task RemoveFinishedAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        // Completed, Failed, Cancelled and Skipped.
        command.CommandText = "DELETE FROM queue WHERE status IN (3, 4, 5, 6);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads the persisted queue. Rows that cannot be deserialised are skipped and logged rather
    /// than failing startup, so one bad row cannot make the application unusable.
    /// </summary>
    public async Task<IReadOnlyList<DownloadTask>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, ordinal, status, stage, created_at, started_at, attempt_count, working_dir, request_json
            FROM queue
            ORDER BY ordinal;
            """;

        var tasks = new List<DownloadTask>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            try
            {
                var request = Deserialize(reader.GetString(8));
                if (request is null) continue;

                tasks.Add(new DownloadTask
                {
                    Id = id,
                    Request = request,
                    Order = reader.GetInt32(1),
                    Status = (DownloadStatus)reader.GetInt32(2),
                    Stage = (DownloadStage)reader.GetInt32(3),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(4), null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    StartedAt = reader.IsDBNull(5)
                        ? null
                        : DateTimeOffset.Parse(reader.GetString(5), null,
                            System.Globalization.DateTimeStyles.RoundtripKind),
                    AttemptCount = reader.GetInt32(6),
                    WorkingDirectory = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Skipping unreadable queue row {Id}", id);
            }
        }

        return tasks;
    }

    private static string Serialize(DownloadRequest request)
    {
        var item = request.Item;

        var persisted = new PersistedRequest
        {
            Url = request.Url,
            ItemId = item.Id,
            Title = item.Title,
            Author = item.DisplayAuthor,
            ThumbnailUrl = item.BestThumbnail?.Url,
            DurationSeconds = item.Duration?.TotalSeconds,
            Kind = item.Kind,
            Mode = request.Mode,
            Selection = request.Selection,
            Container = request.Container,
            AudioFormat = request.AudioFormat,
            LossyQuality = request.LossyQuality,
            Subtitles = request.Subtitles,
            EmbedThumbnail = request.EmbedThumbnail,
            WriteThumbnailFile = request.WriteThumbnailFile,
            EmbedMetadata = request.EmbedMetadata,
            EmbedChapters = request.EmbedChapters,
            TargetDirectory = request.TargetDirectory,
            TargetFileName = request.TargetFileName,
            ExistingFilePolicy = request.ExistingFilePolicy,
            Music = request.Music,
            CollectionId = request.CollectionId,
            CollectionTitle = request.CollectionTitle,
            IndexInCollection = request.IndexInCollection,
        };

        return JsonSerializer.Serialize(persisted, SerializerOptions);
    }

    private static DownloadRequest? Deserialize(string json)
    {
        var persisted = JsonSerializer.Deserialize<PersistedRequest>(json, SerializerOptions);
        if (persisted is null) return null;

        // Rebuilt as a stub: the executor sees no formats and fetches fresh ones before starting,
        // which is exactly what a job resumed hours later needs.
        var item = new MediaItem
        {
            Id = persisted.ItemId,
            Title = persisted.Title,
            WebpageUrl = persisted.Url,
            ChannelName = persisted.Author,
            Duration = persisted.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            Kind = persisted.Kind,
            Music = persisted.Music,
            Thumbnails = persisted.ThumbnailUrl is { Length: > 0 } url
                ? [new ThumbnailInfo { Url = url }]
                : [],
            IsStub = true,
        };

        return new DownloadRequest
        {
            Url = persisted.Url,
            Item = item,
            Mode = persisted.Mode,
            Selection = persisted.Selection,
            Container = persisted.Container,
            AudioFormat = persisted.AudioFormat,
            LossyQuality = persisted.LossyQuality,
            Subtitles = persisted.Subtitles,
            EmbedThumbnail = persisted.EmbedThumbnail,
            WriteThumbnailFile = persisted.WriteThumbnailFile,
            EmbedMetadata = persisted.EmbedMetadata,
            EmbedChapters = persisted.EmbedChapters,
            TargetDirectory = persisted.TargetDirectory,
            TargetFileName = persisted.TargetFileName,
            ExistingFilePolicy = persisted.ExistingFilePolicy,
            Music = persisted.Music,
            CollectionId = persisted.CollectionId,
            CollectionTitle = persisted.CollectionTitle,
            IndexInCollection = persisted.IndexInCollection,
        };
    }
}

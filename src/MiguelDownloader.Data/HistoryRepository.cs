using MiguelDownloader.Core.Downloads;
using MiguelDownloader.Core.Errors;
using MiguelDownloader.Core.Models;
using Microsoft.Data.Sqlite;

namespace MiguelDownloader.Data;

/// <summary>One completed (or failed) download, as stored.</summary>
public sealed record HistoryEntry
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public required string Url { get; init; }
    public required string Title { get; init; }
    public string? Author { get; init; }
    public MediaKind Kind { get; init; }
    public DownloadMode Mode { get; init; }
    public string? ThumbnailUrl { get; init; }
    public string? FilePath { get; init; }
    public long? FileSize { get; init; }
    public string? Container { get; init; }
    public string? QualityLabel { get; init; }
    public TimeSpan? Duration { get; init; }
    public DownloadStatus Status { get; init; }
    public DownloadErrorKind? ErrorKind { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? CollectionTitle { get; init; }

    /// <summary>
    /// Whether the file is still where it was written. Checked on demand rather than stored,
    /// because a file can be moved or deleted at any time by anything.
    /// </summary>
    public bool FileExists => FilePath is { Length: > 0 } && File.Exists(FilePath);
}

/// <summary>How to filter and order the history list.</summary>
public sealed record HistoryQuery
{
    /// <summary>Matches title, author or URL. Null means no text filter.</summary>
    public string? SearchText { get; init; }

    public DownloadStatus? Status { get; init; }
    public DownloadMode? Mode { get; init; }

    /// <summary>Only entries completed on or after this moment.</summary>
    public DateTimeOffset? Since { get; init; }

    public HistorySort Sort { get; init; } = HistorySort.NewestFirst;
    public int Limit { get; init; } = 500;
    public int Offset { get; init; }
}

public enum HistorySort
{
    NewestFirst = 0,
    OldestFirst,
    TitleAscending,
    LargestFirst,
}

/// <summary>Reads and writes the download history.</summary>
public sealed class HistoryRepository(AppDatabase database)
{
    private readonly AppDatabase _database = database;

    /// <summary>Inserts an entry, replacing any previous row with the same id.</summary>
    public async Task AddAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR REPLACE INTO history
                (id, source_id, url, title, author, kind, mode, thumbnail_url, file_path,
                 file_size, container, quality_label, duration_seconds, status, error_kind,
                 completed_at, collection_title)
            VALUES
                (@id, @source_id, @url, @title, @author, @kind, @mode, @thumbnail_url, @file_path,
                 @file_size, @container, @quality_label, @duration, @status, @error_kind,
                 @completed_at, @collection_title);
            """;

        command.Parameters.AddWithValue("@id", entry.Id);
        command.Parameters.AddWithValue("@source_id", entry.SourceId);
        command.Parameters.AddWithValue("@url", entry.Url);
        command.Parameters.AddWithValue("@title", entry.Title);
        command.Parameters.AddWithValue("@author", (object?)entry.Author ?? DBNull.Value);
        command.Parameters.AddWithValue("@kind", (int)entry.Kind);
        command.Parameters.AddWithValue("@mode", (int)entry.Mode);
        command.Parameters.AddWithValue("@thumbnail_url", (object?)entry.ThumbnailUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_path", (object?)entry.FilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@file_size", (object?)entry.FileSize ?? DBNull.Value);
        command.Parameters.AddWithValue("@container", (object?)entry.Container ?? DBNull.Value);
        command.Parameters.AddWithValue("@quality_label", (object?)entry.QualityLabel ?? DBNull.Value);
        command.Parameters.AddWithValue("@duration",
            entry.Duration is { } d ? d.TotalSeconds : (object)DBNull.Value);
        command.Parameters.AddWithValue("@status", (int)entry.Status);
        command.Parameters.AddWithValue("@error_kind",
            entry.ErrorKind is { } k ? (int)k : (object)DBNull.Value);
        command.Parameters.AddWithValue("@completed_at", entry.CompletedAt.ToString("O"));
        command.Parameters.AddWithValue("@collection_title", (object?)entry.CollectionTitle ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes many entries in one transaction. Finishing a large playlist would otherwise commit
    /// once per track, which is slow enough to be noticeable while the queue drains.
    /// </summary>
    public async Task AddManyAsync(
        IReadOnlyCollection<HistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        await using var connection = _database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO history
                (id, source_id, url, title, author, kind, mode, thumbnail_url, file_path,
                 file_size, container, quality_label, duration_seconds, status, error_kind,
                 completed_at, collection_title)
            VALUES
                (@id, @source_id, @url, @title, @author, @kind, @mode, @thumbnail_url, @file_path,
                 @file_size, @container, @quality_label, @duration, @status, @error_kind,
                 @completed_at, @collection_title);
            """;

        var p = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[]
                 {
                     "@id", "@source_id", "@url", "@title", "@author", "@kind", "@mode",
                     "@thumbnail_url", "@file_path", "@file_size", "@container", "@quality_label",
                     "@duration", "@status", "@error_kind", "@completed_at", "@collection_title",
                 })
        {
            p[name] = command.Parameters.Add(name, SqliteType.Text);
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            p["@id"].Value = entry.Id;
            p["@source_id"].Value = entry.SourceId;
            p["@url"].Value = entry.Url;
            p["@title"].Value = entry.Title;
            p["@author"].Value = (object?)entry.Author ?? DBNull.Value;
            p["@kind"].Value = (int)entry.Kind;
            p["@mode"].Value = (int)entry.Mode;
            p["@thumbnail_url"].Value = (object?)entry.ThumbnailUrl ?? DBNull.Value;
            p["@file_path"].Value = (object?)entry.FilePath ?? DBNull.Value;
            p["@file_size"].Value = (object?)entry.FileSize ?? DBNull.Value;
            p["@container"].Value = (object?)entry.Container ?? DBNull.Value;
            p["@quality_label"].Value = (object?)entry.QualityLabel ?? DBNull.Value;
            p["@duration"].Value = entry.Duration is { } d ? d.TotalSeconds : (object)DBNull.Value;
            p["@status"].Value = (int)entry.Status;
            p["@error_kind"].Value = entry.ErrorKind is { } k ? (int)k : (object)DBNull.Value;
            p["@completed_at"].Value = entry.CompletedAt.ToString("O");
            p["@collection_title"].Value = (object?)entry.CollectionTitle ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a filtered query.</summary>
    public async Task<IReadOnlyList<HistoryEntry>> QueryAsync(
        HistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            // Parameterised LIKE: the pattern is a value, never concatenated into the statement.
            // SQLite has no default escape character, so one is declared explicitly; without it a
            // search for "100%" would match every row.
            where.Add(
                "(title LIKE @search ESCAPE '\\' OR author LIKE @search ESCAPE '\\' " +
                "OR url LIKE @search ESCAPE '\\')");
            command.Parameters.AddWithValue("@search", $"%{Escape(query.SearchText)}%");
        }

        if (query.Status is { } status)
        {
            where.Add("status = @status");
            command.Parameters.AddWithValue("@status", (int)status);
        }

        if (query.Mode is { } mode)
        {
            where.Add("mode = @mode");
            command.Parameters.AddWithValue("@mode", (int)mode);
        }

        if (query.Since is { } since)
        {
            where.Add("completed_at >= @since");
            command.Parameters.AddWithValue("@since", since.ToString("O"));
        }

        // The ORDER BY clause is chosen from a closed set, never built from user input.
        var order = query.Sort switch
        {
            HistorySort.OldestFirst => "completed_at ASC",
            HistorySort.TitleAscending => "title COLLATE NOCASE ASC",
            HistorySort.LargestFirst => "file_size DESC",
            _ => "completed_at DESC",
        };

        command.CommandText =
            "SELECT id, source_id, url, title, author, kind, mode, thumbnail_url, file_path, " +
            "       file_size, container, quality_label, duration_seconds, status, error_kind, " +
            "       completed_at, collection_title " +
            "FROM history " +
            (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : string.Empty) +
            $"ORDER BY {order} LIMIT @limit OFFSET @offset;";

        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 5000));
        command.Parameters.AddWithValue("@offset", Math.Max(0, query.Offset));

        var results = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(Read(reader));

        return results;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value ?? 0);
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HistoryEntry Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        SourceId = reader.GetString(1),
        Url = reader.GetString(2),
        Title = reader.GetString(3),
        Author = reader.IsDBNull(4) ? null : reader.GetString(4),
        Kind = (MediaKind)reader.GetInt32(5),
        Mode = (DownloadMode)reader.GetInt32(6),
        ThumbnailUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
        FilePath = reader.IsDBNull(8) ? null : reader.GetString(8),
        FileSize = reader.IsDBNull(9) ? null : reader.GetInt64(9),
        Container = reader.IsDBNull(10) ? null : reader.GetString(10),
        QualityLabel = reader.IsDBNull(11) ? null : reader.GetString(11),
        Duration = reader.IsDBNull(12) ? null : TimeSpan.FromSeconds(reader.GetDouble(12)),
        Status = (DownloadStatus)reader.GetInt32(13),
        ErrorKind = reader.IsDBNull(14) ? null : (DownloadErrorKind)reader.GetInt32(14),
        CompletedAt = DateTimeOffset.Parse(reader.GetString(15), null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        CollectionTitle = reader.IsDBNull(16) ? null : reader.GetString(16),
    };

    /// <summary>
    /// Neutralises the LIKE wildcards, matching the <c>ESCAPE '\'</c> clause on the query.
    /// The backslash itself is escaped first, otherwise it would consume the escape added after it.
    /// </summary>
    internal static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
}

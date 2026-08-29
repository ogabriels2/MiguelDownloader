using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MiguelDownloader.Data;

/// <summary>
/// Owns the SQLite file and its schema.
/// <para>
/// The schema is versioned through <c>user_version</c> and upgraded by running the migrations
/// newer than the file's current version. Each migration runs inside a transaction, so an
/// interrupted upgrade leaves the database on its previous version rather than half-migrated.
/// </para>
/// </summary>
public sealed class AppDatabase(string databasePath, ILogger<AppDatabase> logger)
{
    private readonly string _databasePath = databasePath;
    private readonly ILogger<AppDatabase> _logger = logger;

    /// <summary>Default location, alongside the rest of the application data.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiguelDownloader", "migueldownloader.db");

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // The UI thread reads while the queue writes, so pooled connections share the file.
        Cache = SqliteCacheMode.Shared,
    }.ToString();

    /// <summary>Opens a connection with the pragmas this application relies on.</summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        // Write-ahead logging lets readers continue while a write is in flight, which matters
        // because the queue writes on every progress milestone while the history page reads.
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>Creates the file if needed and applies any outstanding migrations.</summary>
    public void Initialize()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var connection = OpenConnection();

        var current = GetSchemaVersion(connection);
        var migrations = Migrations;

        if (current >= migrations.Length)
        {
            _logger.LogDebug("Database schema is current (version {Version})", current);
            return;
        }

        _logger.LogInformation("Upgrading database schema from {From} to {To}", current, migrations.Length);

        for (var version = current; version < migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = migrations[version];
            command.ExecuteNonQuery();

            // user_version does not accept a parameter, and the value is an int we control.
            using var setVersion = connection.CreateCommand();
            setVersion.Transaction = transaction;
            setVersion.CommandText = $"PRAGMA user_version = {version + 1};";
            setVersion.ExecuteNonQuery();

            transaction.Commit();
            _logger.LogDebug("Applied migration {Version}", version + 1);
        }
    }

    private static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Schema migrations, in order. Append only: an existing entry must never be edited, because
    /// databases in the field have already run it.
    /// </summary>
    private static string[] Migrations =>
    [
        // 1 - history and the persisted queue
        """
        CREATE TABLE history (
            id                TEXT    PRIMARY KEY,
            source_id         TEXT    NOT NULL,
            url               TEXT    NOT NULL,
            title             TEXT    NOT NULL,
            author            TEXT,
            kind              INTEGER NOT NULL,
            mode              INTEGER NOT NULL,
            thumbnail_url     TEXT,
            file_path         TEXT,
            file_size         INTEGER,
            container         TEXT,
            quality_label     TEXT,
            duration_seconds  REAL,
            status            INTEGER NOT NULL,
            error_kind        INTEGER,
            completed_at      TEXT    NOT NULL,
            collection_title  TEXT
        );

        CREATE INDEX ix_history_completed_at ON history (completed_at DESC);
        CREATE INDEX ix_history_source_id    ON history (source_id);

        CREATE TABLE queue (
            id            TEXT    PRIMARY KEY,
            ordinal       INTEGER NOT NULL,
            status        INTEGER NOT NULL,
            stage         INTEGER NOT NULL,
            created_at    TEXT    NOT NULL,
            started_at    TEXT,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            working_dir   TEXT,
            request_json  TEXT    NOT NULL
        );

        CREATE INDEX ix_queue_ordinal ON queue (ordinal);
        """,
    ];

    /// <summary>Removes rows older than the retention window. Returns how many were deleted.</summary>
    public int PruneHistory(int keepDays)
    {
        if (keepDays <= 0) return 0;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history WHERE completed_at < @cutoff;";
        command.Parameters.AddWithValue("@cutoff",
            DateTimeOffset.Now.AddDays(-keepDays).ToString("O"));

        return command.ExecuteNonQuery();
    }

    /// <summary>Reclaims space after large deletions.</summary>
    public void Compact()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }
}

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Database;

/// <summary>
/// Stores transfers in a SQLite file under the per-user application data folder. The
/// schema is versioned with SQLite's own <c>user_version</c> pragma, so upgrading is a
/// matter of appending to <see cref="Migrations"/>.
/// </summary>
public sealed class SqliteDownloadRepository : IDownloadRepository
{
    /// <summary>
    /// Applied in order; the pragma records how many have run. Never edit an entry that
    /// has shipped — append a new one instead, or existing databases will diverge.
    /// </summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE downloads (
            id               TEXT    NOT NULL PRIMARY KEY,
            address          TEXT    NOT NULL,
            destination_path TEXT        NULL,
            bytes_received   INTEGER NOT NULL DEFAULT 0,
            total_bytes      INTEGER     NULL,
            status           INTEGER NOT NULL,
            detail           TEXT        NULL,
            created_at       TEXT    NOT NULL,
            updated_at       TEXT    NOT NULL
        );
        CREATE INDEX ix_downloads_created_at ON downloads (created_at DESC);
        """,
        """
        ALTER TABLE downloads ADD COLUMN media_type TEXT NULL;
        """,

        // Whether the destination came from a save dialog. Without it a restored row
        // could not tell a folder the user picked from one SDM sorted the file into, so
        // resuming a transfer saved somewhere else looked in the wrong place, found no
        // partial file, and started the whole download again into the default folder.
        """
        ALTER TABLE downloads ADD COLUMN chosen_by_user INTEGER NOT NULL DEFAULT 0;
        """,
    ];

    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly ILogger<SqliteDownloadRepository> _logger;
    private readonly string _connectionString;

    private bool _initialized;

    public SqliteDownloadRepository(
        IOptions<DownloadStorageOptions> options,
        ILogger<SqliteDownloadRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        string directory = string.IsNullOrWhiteSpace(options.Value.DirectoryPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDM")
            : options.Value.DirectoryPath;

        Directory.CreateDirectory(directory);

        DatabasePath = Path.Combine(directory, options.Value.FileName);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, address, destination_path, bytes_received, total_bytes,
                   status, detail, created_at, updated_at, media_type, chosen_by_user
            FROM downloads
            ORDER BY created_at DESC;
            """;

        List<DownloadJob> jobs = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(new DownloadJob
            {
                Id = Guid.Parse(reader.GetString(0)),
                Address = reader.GetString(1),
                DestinationPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                BytesReceived = reader.GetInt64(3),
                TotalBytes = reader.IsDBNull(4) ? null : reader.GetInt64(4),
                Status = (DownloadStatus)reader.GetInt32(5),
                Detail = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = ReadTimestamp(reader, 7),
                UpdatedAt = ReadTimestamp(reader, 8),
                MediaType = reader.IsDBNull(9) ? null : reader.GetString(9),
                ChosenByUser = reader.GetInt32(10) != 0,
                Category = FileCategories.Resolve(
                    reader.IsDBNull(2) ? null : Path.GetFileName(reader.GetString(2)),
                    reader.IsDBNull(9) ? null : reader.GetString(9)),
            });
        }

        return jobs;
    }

    public async Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO downloads
                (id, address, destination_path, bytes_received, total_bytes,
                 status, detail, created_at, updated_at, media_type, chosen_by_user)
            VALUES
                ($id, $address, $destination, $received, $total,
                 $status, $detail, $created, $updated, $media, $chosen)
            ON CONFLICT(id) DO UPDATE SET
                address          = excluded.address,
                destination_path = excluded.destination_path,
                bytes_received   = excluded.bytes_received,
                total_bytes      = excluded.total_bytes,
                status           = excluded.status,
                detail           = excluded.detail,
                media_type       = excluded.media_type,
                chosen_by_user   = excluded.chosen_by_user,
                updated_at       = excluded.updated_at
            WHERE excluded.updated_at >= downloads.updated_at;
            """;

        // The WHERE above is what keeps an older snapshot from winning. Rows save
        // themselves without being awaited, from several transfers at once, so the order
        // the writes reach SQLite in is not the order they were taken in — and a row that
        // had just completed could be put back to "Downloading" by a progress snapshot
        // taken a moment earlier. Timestamps are round-trip and always UTC, so comparing
        // them as text compares them as instants.
        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$address", job.Address);
        command.Parameters.AddWithValue("$destination", (object?)job.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$received", job.BytesReceived);
        command.Parameters.AddWithValue("$total", (object?)job.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)job.Status);
        command.Parameters.AddWithValue("$detail", (object?)job.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$media", (object?)job.MediaType ?? DBNull.Value);
        command.Parameters.AddWithValue("$chosen", job.ChosenByUser ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM downloads WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a timestamp back the way it was written: round-trip format, invariant
    /// culture, and no reinterpretation of the offset it already carries.
    ///
    /// These are written with "O" and were being read with the current culture. On a
    /// machine whose culture uses a non-Gregorian calendar — an Arabic Windows with the
    /// Hijri calendar, say — "2026-09-04T…" is read as a year in that calendar, so the
    /// whole list either fails to load or comes back with dates that sort wrongly. What a
    /// row was written by has nothing to do with what a person's machine displays.
    /// </summary>
    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.ParseExact(
            reader.GetString(ordinal),
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Per connection, and cheap. Rows save themselves from several transfers at once
        // and a writer holds the database briefly; without this the loser gets "database
        // is locked" at once rather than waiting the moment out, and a row's state is
        // simply lost. Waiting is the correct answer to a lock that lasts milliseconds.
        SqliteCommand busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout = 5000;";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_initialized)
            {
                return;
            }

            await MigrateAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initialization.Release();
        }
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Set once and recorded in the file itself, so this is the only place it belongs.
        // The default journal makes a reader and a writer exclude each other, and this
        // list is read while several transfers are writing to it. Under write-ahead
        // logging they do not block one another at all.
        SqliteCommand journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode = WAL;";
        await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        SqliteCommand read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        int applied = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), null);

        if (applied >= Migrations.Length)
        {
            return;
        }

        await using SqliteTransaction transaction = (SqliteTransaction)
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        for (int version = applied; version < Migrations.Length; version++)
        {
            SqliteCommand migrate = connection.CreateCommand();
            migrate.Transaction = transaction;

            // The pragma takes a literal, so it cannot be parameterised. The value is a
            // loop counter over a private array, never anything a user supplied.
            migrate.CommandText = Migrations[version] + $"\nPRAGMA user_version = {version + 1};";
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Download database ready at {DatabasePath}; schema version {Version}.",
            DatabasePath,
            Migrations.Length);
    }
}

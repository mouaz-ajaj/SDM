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
                   status, detail, created_at, updated_at, media_type
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
                CreatedAt = DateTimeOffset.Parse(reader.GetString(7), null),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(8), null),
                MediaType = reader.IsDBNull(9) ? null : reader.GetString(9),
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
                 status, detail, created_at, updated_at, media_type)
            VALUES
                ($id, $address, $destination, $received, $total,
                 $status, $detail, $created, $updated, $media)
            ON CONFLICT(id) DO UPDATE SET
                address          = excluded.address,
                destination_path = excluded.destination_path,
                bytes_received   = excluded.bytes_received,
                total_bytes      = excluded.total_bytes,
                status           = excluded.status,
                detail           = excluded.detail,
                media_type       = excluded.media_type,
                updated_at       = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$id", job.Id.ToString());
        command.Parameters.AddWithValue("$address", job.Address);
        command.Parameters.AddWithValue("$destination", (object?)job.DestinationPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$received", job.BytesReceived);
        command.Parameters.AddWithValue("$total", (object?)job.TotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)job.Status);
        command.Parameters.AddWithValue("$detail", (object?)job.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$media", (object?)job.MediaType ?? DBNull.Value);

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

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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

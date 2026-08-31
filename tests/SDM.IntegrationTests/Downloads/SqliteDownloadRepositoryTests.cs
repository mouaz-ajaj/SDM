using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;
using SDM.Database;

namespace SDM.IntegrationTests.Downloads;

public sealed class SqliteDownloadRepositoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sdm-db-").FullName;

    [Fact]
    public async Task SaveAsync_ThenGetAllAsync_ReturnsTheJobUnchanged()
    {
        SqliteDownloadRepository repository = Create();
        DownloadJob job = NewJob() with
        {
            DestinationPath = @"C:\Downloads\file.bin",
            BytesReceived = 1_048_576,
            TotalBytes = 1_073_741_824,
            Status = DownloadStatus.Paused,
            Detail = "Paused at 1 MB of 1 GB",
        };

        await repository.SaveAsync(job, TestContext.Current.CancellationToken);
        DownloadJob stored = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(job.Id, stored.Id);
        Assert.Equal(job.Address, stored.Address);
        Assert.Equal(job.DestinationPath, stored.DestinationPath);
        Assert.Equal(job.BytesReceived, stored.BytesReceived);
        Assert.Equal(job.TotalBytes, stored.TotalBytes);
        Assert.Equal(DownloadStatus.Paused, stored.Status);
        Assert.Equal(job.Detail, stored.Detail);
    }

    [Fact]
    public async Task SaveAsync_UpdatesAnExistingJobRatherThanDuplicatingIt()
    {
        SqliteDownloadRepository repository = Create();
        DownloadJob job = NewJob();

        await repository.SaveAsync(job, TestContext.Current.CancellationToken);
        await repository.SaveAsync(
            job with { Status = DownloadStatus.Completed, BytesReceived = 4096 },
            TestContext.Current.CancellationToken);

        DownloadJob stored = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DownloadStatus.Completed, stored.Status);
        Assert.Equal(4096, stored.BytesReceived);
    }

    [Fact]
    public async Task GetAllAsync_KeepsNullableColumnsNull()
    {
        SqliteDownloadRepository repository = Create();

        await repository.SaveAsync(NewJob(), TestContext.Current.CancellationToken);
        DownloadJob stored = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Null(stored.DestinationPath);
        Assert.Null(stored.TotalBytes);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheRequestedJob()
    {
        SqliteDownloadRepository repository = Create();
        DownloadJob kept = NewJob();
        DownloadJob removed = NewJob();

        await repository.SaveAsync(kept, TestContext.Current.CancellationToken);
        await repository.SaveAsync(removed, TestContext.Current.CancellationToken);
        await repository.DeleteAsync(removed.Id, TestContext.Current.CancellationToken);

        DownloadJob stored = Assert.Single(await repository.GetAllAsync(TestContext.Current.CancellationToken));
        Assert.Equal(kept.Id, stored.Id);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsNewestFirst()
    {
        SqliteDownloadRepository repository = Create();
        DownloadJob older = NewJob() with { CreatedAt = DateTimeOffset.UtcNow.AddHours(-1) };
        DownloadJob newer = NewJob();

        await repository.SaveAsync(older, TestContext.Current.CancellationToken);
        await repository.SaveAsync(newer, TestContext.Current.CancellationToken);

        IReadOnlyList<DownloadJob> stored = await repository.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(newer.Id, stored[0].Id);
        Assert.Equal(older.Id, stored[1].Id);
    }

    [Fact]
    public async Task Repository_SurvivesBeingReopened()
    {
        // The point of the whole phase: a second process must see the first one's work.
        DownloadJob job = NewJob() with { Status = DownloadStatus.Paused };
        await Create().SaveAsync(job, TestContext.Current.CancellationToken);

        SqliteDownloadRepository reopened = Create();
        DownloadJob stored = Assert.Single(await reopened.GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(job.Id, stored.Id);
        Assert.Equal(DownloadStatus.Paused, stored.Status);
    }

    [Fact]
    public async Task Repository_MigratesOnlyOnceAcrossInstances()
    {
        await Create().SaveAsync(NewJob(), TestContext.Current.CancellationToken);

        // A second migration pass over an existing schema would throw "table already exists".
        Exception? exception = await Record.ExceptionAsync(
            () => Create().GetAllAsync(TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public void Repository_PlacesTheDatabaseInTheConfiguredDirectory()
    {
        SqliteDownloadRepository repository = Create();

        Assert.Equal(Path.Combine(_directory, "sdm.db"), repository.DatabasePath);
    }

    private SqliteDownloadRepository Create() =>
        new(
            Options.Create(new DownloadStorageOptions { DirectoryPath = _directory, FileName = "sdm.db" }),
            NullLogger<SqliteDownloadRepository>.Instance);

    private static DownloadJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Address = "https://example.test/file.bin",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp database must not fail an otherwise passing test.
        }
    }
}

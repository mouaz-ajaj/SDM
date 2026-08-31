using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Application.Tests.Downloads;

public sealed class StartDownloadUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesATransientFailure()
    {
        ScriptedEngine engine = new(
            new DownloadFailedException("Server answered 429", 429, null, isTransient: true),
            new DownloadFailedException("Server answered 503", 503, null, isTransient: true));

        List<DownloadRetry> retries = [];
        StartDownloadUseCase useCase = Create(engine);

        DownloadResult result = await useCase.ExecuteAsync(
            "https://example.test/file.bin",
            new DownloadCallbacks { Retrying = retries.Add },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, engine.Attempts);
        Assert.Equal(2, retries.Count);
        Assert.Equal(1, retries[0].Attempt);
        Assert.Contains("429", retries[0].Reason, StringComparison.Ordinal);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsRetryingAfterBytesHaveArrivedBecauseTheEngineResumes()
    {
        // Phase 2.4 refused to retry once anything had transferred, because a restart
        // would have discarded it. The engine now continues from its partial file, so
        // this is exactly the case retrying is most valuable for.
        ScriptedEngine engine = new(new DownloadFailedException(
            "The connection failed after 900000000 bytes.", null, null, isTransient: true))
        {
            BytesBeforeFailure = 900_000_000,
        };

        StartDownloadUseCase useCase = Create(engine);

        await useCase.ExecuteAsync(
            "https://example.test/file.bin", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, engine.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryAPermanentFailure()
    {
        ScriptedEngine engine = new(new DownloadFailedException(
            "Server answered 404", 404, null, isTransient: false));

        StartDownloadUseCase useCase = Create(engine);

        await Assert.ThrowsAsync<DownloadFailedException>(() => useCase.ExecuteAsync(
            "https://example.test/file.bin", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, engine.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_HonoursRetryAfterInPreferenceToBackoff()
    {
        ScriptedEngine engine = new(new DownloadFailedException(
            "Server answered 429", 429, TimeSpan.FromSeconds(2), isTransient: true));

        List<DownloadRetry> retries = [];
        StartDownloadUseCase useCase = Create(engine);

        await useCase.ExecuteAsync(
            "https://example.test/file.bin",
            new DownloadCallbacks { Retrying = retries.Add },
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(2), retries[0].Delay);
    }

    [Fact]
    public async Task ExecuteAsync_CapsAnUnreasonableRetryAfter()
    {
        ScriptedEngine engine = new(new DownloadFailedException(
            "Server answered 503", 503, TimeSpan.FromHours(3), isTransient: true));

        List<DownloadRetry> retries = [];
        StartDownloadUseCase useCase = Create(engine, maximumRetryDelaySeconds: 1);

        await useCase.ExecuteAsync(
            "https://example.test/file.bin",
            new DownloadCallbacks { Retrying = retries.Add },
            TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(1), retries[0].Delay);
    }

    [Fact]
    public async Task ExecuteAsync_GivesUpAfterTheConfiguredNumberOfAttempts()
    {
        ScriptedEngine engine = new(Enumerable.Range(0, 10)
            .Select(_ => new DownloadFailedException("Server answered 429", 429, TimeSpan.Zero, true))
            .ToArray());

        StartDownloadUseCase useCase = Create(engine, maximumAttempts: 3);

        await Assert.ThrowsAsync<DownloadFailedException>(() => useCase.ExecuteAsync(
            "https://example.test/file.bin", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(3, engine.Attempts);
    }

    [Fact]
    public void Discard_RemovesThePartialFileForAnAbandonedTransfer()
    {
        ScriptedEngine engine = new();
        StartDownloadUseCase useCase = Create(engine);

        useCase.Discard(@"C:\Downloads\file.bin");

        Assert.Equal(@"C:\Downloads\file.bin", engine.Discarded);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.test/file.bin")]
    public async Task ExecuteAsync_RejectsAddressesItCannotDownload(string address)
    {
        StartDownloadUseCase useCase = Create(new ScriptedEngine());

        await Assert.ThrowsAsync<ArgumentException>(() => useCase.ExecuteAsync(
            address, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static StartDownloadUseCase Create(
        IDownloadEngine engine,
        int maximumAttempts = 4,
        int maximumRetryDelaySeconds = 60) =>
        new(engine, new FixedFolder(), Options.Create(new DownloadOptions
        {
            MaximumAttempts = maximumAttempts,
            MaximumRetryDelaySeconds = maximumRetryDelaySeconds,
        }));

    private sealed class FixedFolder : IDownloadFolder
    {
        public string GetPath() => Path.GetTempPath();
    }

    /// <summary>Throws the queued failures in order, then succeeds.</summary>
    private sealed class ScriptedEngine(params DownloadFailedException[] failures) : IDownloadEngine
    {
        private int _index;

        public int Attempts { get; private set; }

        public string? Discarded { get; private set; }

        public long BytesBeforeFailure { get; init; }

        public Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            DownloadCallbacks? callbacks = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;

            if (BytesBeforeFailure > 0)
            {
                callbacks?.Progress?.Report(
                    new DownloadProgress(BytesBeforeFailure, BytesBeforeFailure * 2));
            }

            if (_index < failures.Length)
            {
                throw failures[_index++];
            }

            return Task.FromResult(new DownloadResult(
                Path.Combine(request.DestinationDirectory, "file.bin"), 1024));
        }

        public void DiscardPartial(string destinationPath) => Discarded = destinationPath;
    }
}

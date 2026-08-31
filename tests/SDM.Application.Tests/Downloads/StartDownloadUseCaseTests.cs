using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Application.Tests.Downloads;

public sealed class StartDownloadUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_RetriesATransientFailureThatDeliveredNoBytes()
    {
        ScriptedEngine engine = new(
            new DownloadFailedException("Server answered 429", 429, null, isTransient: true),
            new DownloadFailedException("Server answered 503", 503, null, isTransient: true));

        List<DownloadRetry> retries = [];
        StartDownloadUseCase useCase = Create(engine);

        DownloadResult result = await useCase.ExecuteAsync(
            "https://example.test/file.bin", onRetry: retries.Add,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, engine.Attempts);
        Assert.Equal(2, retries.Count);
        Assert.Equal(1, retries[0].Attempt);
        Assert.Contains("429", retries[0].Reason, StringComparison.Ordinal);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryOnceBytesHaveArrived()
    {
        // Retrying here would throw away everything already transferred and start over.
        ScriptedEngine engine = new(new DownloadFailedException(
            "The connection failed after 900000000 bytes.", null, null, isTransient: true))
        {
            BytesBeforeFailure = 900_000_000,
        };

        StartDownloadUseCase useCase = Create(engine);

        await Assert.ThrowsAsync<DownloadFailedException>(() => useCase.ExecuteAsync(
            "https://example.test/file.bin", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, engine.Attempts);
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
            "https://example.test/file.bin", onRetry: retries.Add,
            cancellationToken: TestContext.Current.CancellationToken);

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
            "https://example.test/file.bin", onRetry: retries.Add,
            cancellationToken: TestContext.Current.CancellationToken);

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

        public long BytesBeforeFailure { get; init; }

        public Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Attempts++;

            if (BytesBeforeFailure > 0)
            {
                progress?.Report(new DownloadProgress(BytesBeforeFailure, BytesBeforeFailure * 2));
            }

            if (_index < failures.Length)
            {
                throw failures[_index++];
            }

            return Task.FromResult(new DownloadResult(
                Path.Combine(request.DestinationDirectory, "file.bin"), 1024));
        }
    }
}

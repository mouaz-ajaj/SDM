using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Application.Tests.Downloads;

public sealed class DownloadSchedulerTests
{
    [Fact]
    public async Task EnqueueAsync_NeverRunsMoreTransfersThanTheConfiguredLimit()
    {
        const int Limit = 3;
        const int Requested = 9;

        GateKeepingUseCase useCase = new();
        using DownloadScheduler scheduler = Create(useCase, Limit);

        Task[] transfers = [.. Enumerable.Range(0, Requested)
            .Select(index => scheduler.EnqueueAsync(
                $"https://example.test/file{index}.bin",
                cancellationToken: TestContext.Current.CancellationToken))];

        // Let every slot fill, then release them one at a time so the peak is observable.
        await useCase.WaitForRunningAsync(Limit, TestContext.Current.CancellationToken);
        useCase.ReleaseAll();
        await Task.WhenAll(transfers);

        Assert.Equal(Requested, useCase.Completed);
        Assert.Equal(Limit, useCase.PeakConcurrency);
    }

    [Fact]
    public async Task EnqueueAsync_ReportsStartedOnlyWhenASlotIsAcquired()
    {
        GateKeepingUseCase useCase = new();
        using DownloadScheduler scheduler = Create(useCase, maximumConcurrent: 1);

        bool firstStarted = false;
        bool secondStarted = false;

        Task first = scheduler.EnqueueAsync(
            "https://example.test/a.bin", new DownloadCallbacks { Started = () => firstStarted = true },
            cancellationToken: TestContext.Current.CancellationToken);
        Task second = scheduler.EnqueueAsync(
            "https://example.test/b.bin", new DownloadCallbacks { Started = () => secondStarted = true },
            cancellationToken: TestContext.Current.CancellationToken);

        await useCase.WaitForRunningAsync(1, TestContext.Current.CancellationToken);

        Assert.True(firstStarted, "The transfer holding the slot should have started.");
        Assert.False(secondStarted, "A queued transfer must not report itself as started.");

        useCase.ReleaseAll();
        await Task.WhenAll(first, second);

        Assert.True(secondStarted);
    }

    [Fact]
    public async Task EnqueueAsync_WhenCancelledWhileQueued_NeverStartsTheTransfer()
    {
        GateKeepingUseCase useCase = new();
        using DownloadScheduler scheduler = Create(useCase, maximumConcurrent: 1);
        using CancellationTokenSource cancellation = new();

        Task holder = scheduler.EnqueueAsync(
            "https://example.test/a.bin", cancellationToken: TestContext.Current.CancellationToken);
        await useCase.WaitForRunningAsync(1, TestContext.Current.CancellationToken);

        Task queued = scheduler.EnqueueAsync("https://example.test/b.bin", cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, useCase.Started);

        useCase.ReleaseAll();
        await holder;
    }

    [Fact]
    public async Task EnqueueAsync_LimitsConcurrencyPerHostSoServersDoNotRejectUs()
    {
        GateKeepingUseCase useCase = new();
        using DownloadScheduler scheduler = Create(useCase, maximumConcurrent: 6, maximumPerHost: 2);

        Task[] sameHost = [.. Enumerable.Range(0, 5).Select(index => scheduler.EnqueueAsync(
            $"https://one.example.test/file{index}.bin",
            cancellationToken: TestContext.Current.CancellationToken))];

        await useCase.WaitForRunningAsync(2, TestContext.Current.CancellationToken);
        await Task.Delay(120, TestContext.Current.CancellationToken);

        Assert.Equal(2, useCase.PeakConcurrency);

        useCase.ReleaseAll();
        await Task.WhenAll(sameHost);
    }

    [Fact]
    public async Task EnqueueAsync_LetsDifferentHostsRunInParallel()
    {
        GateKeepingUseCase useCase = new();
        using DownloadScheduler scheduler = Create(useCase, maximumConcurrent: 3, maximumPerHost: 1);

        Task[] mixed =
        [
            scheduler.EnqueueAsync("https://a.example.test/x.bin", cancellationToken: TestContext.Current.CancellationToken),
            scheduler.EnqueueAsync("https://b.example.test/x.bin", cancellationToken: TestContext.Current.CancellationToken),
            scheduler.EnqueueAsync("https://c.example.test/x.bin", cancellationToken: TestContext.Current.CancellationToken),
        ];

        await useCase.WaitForRunningAsync(3, TestContext.Current.CancellationToken);
        Assert.Equal(3, useCase.PeakConcurrency);

        useCase.ReleaseAll();
        await Task.WhenAll(mixed);
    }

    private static DownloadScheduler Create(
        IStartDownloadUseCase useCase, int maximumConcurrent, int maximumPerHost = 16) =>
        new(useCase, Options.Create(new DownloadOptions
        {
            MaximumConcurrent = maximumConcurrent,
            MaximumPerHost = Math.Min(maximumPerHost, maximumConcurrent),
        }));

    /// <summary>A use case that blocks until released, so concurrency can be observed.</summary>
    private sealed class GateKeepingUseCase : IStartDownloadUseCase
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _sync = new();

        private int _running;

        public int Started { get; private set; }

        public int Completed { get; private set; }

        public int PeakConcurrency { get; private set; }

        public void Discard(string destinationPath)
        {
        }

        public async Task<DownloadResult> ExecuteAsync(
            string address,
            DownloadCallbacks? callbacks = null,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _running++;
                Started++;
                PeakConcurrency = Math.Max(PeakConcurrency, _running);
            }

            await _gate.Task.WaitAsync(cancellationToken);

            lock (_sync)
            {
                _running--;
                Completed++;
            }

            return new DownloadResult(address, 0);
        }

        public async Task WaitForRunningAsync(int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_sync)
                {
                    if (_running >= count)
                    {
                        return;
                    }
                }

                await Task.Delay(10, cancellationToken);
            }
        }

        public void ReleaseAll() => _gate.TrySetResult();
    }
}

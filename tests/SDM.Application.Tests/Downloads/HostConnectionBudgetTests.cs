using Microsoft.Extensions.Options;
using SDM.Application.Downloads;

namespace SDM.Application.Tests.Downloads;

public sealed class HostConnectionBudgetTests
{
    [Fact]
    public async Task AcquireAsync_GivesALoneTransferEverythingItCanSafelyTake()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 6, transfersPerHost: 2);

        using IConnectionLease lease = await budget.AcquireAsync(
            "example.test", desired: 4, TestContext.Current.CancellationToken);

        Assert.Equal(4, lease.Count);
    }

    [Fact]
    public async Task AcquireAsync_HoldsBackAConnectionForEveryOtherPermittedTransfer()
    {
        // Six connections shared by at most two transfers: one is reserved, so a single
        // transfer can never take more than five and strand the other one.
        using HostConnectionBudget budget = Create(connectionsPerHost: 6, transfersPerHost: 2);

        using IConnectionLease greedy = await budget.AcquireAsync(
            "example.test", desired: 99, TestContext.Current.CancellationToken);

        Assert.Equal(5, greedy.Count);
    }

    [Fact]
    public async Task AcquireAsync_NeverStrandsASecondTransferBehindTheFirst()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 6, transfersPerHost: 2);

        using IConnectionLease first = await budget.AcquireAsync(
            "example.test", desired: 99, TestContext.Current.CancellationToken);

        // This is the case that would deadlock if one transfer could take everything:
        // the second must be served without the first having to finish.
        Task<IConnectionLease> second = budget.AcquireAsync(
            "example.test", desired: 99, TestContext.Current.CancellationToken);

        using IConnectionLease granted = await second.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, granted.Count);
        Assert.Equal(6, first.Count + granted.Count);
    }

    [Fact]
    public async Task AcquireAsync_SharesTheBudgetBetweenTransfers()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 4, transfersPerHost: 2);

        using IConnectionLease first = await budget.AcquireAsync(
            "example.test", desired: 2, TestContext.Current.CancellationToken);
        using IConnectionLease second = await budget.AcquireAsync(
            "example.test", desired: 4, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task AcquireAsync_WaitsWhenTheHostHasNothingLeft()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 1, transfersPerHost: 1);

        IConnectionLease held = await budget.AcquireAsync(
            "example.test", desired: 1, TestContext.Current.CancellationToken);

        Task<IConnectionLease> waiting = budget.AcquireAsync(
            "example.test", desired: 1, TestContext.Current.CancellationToken);

        Assert.False(waiting.IsCompleted, "The budget was exhausted, so this must wait.");

        held.Dispose();

        using IConnectionLease granted = await waiting.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, granted.Count);
    }

    [Fact]
    public async Task AcquireAsync_KeepsHostsIndependent()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 2, transfersPerHost: 1);

        using IConnectionLease first = await budget.AcquireAsync(
            "one.test", desired: 2, TestContext.Current.CancellationToken);
        using IConnectionLease second = await budget.AcquireAsync(
            "two.test", desired: 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public async Task Lease_ReturnsItsConnectionsOnlyOnce()
    {
        using HostConnectionBudget budget = Create(connectionsPerHost: 2, transfersPerHost: 1);

        IConnectionLease lease = await budget.AcquireAsync(
            "example.test", desired: 2, TestContext.Current.CancellationToken);

        lease.Dispose();
        lease.Dispose();

        // A double release would inflate the budget past its ceiling.
        using IConnectionLease next = await budget.AcquireAsync(
            "example.test", desired: 99, TestContext.Current.CancellationToken);

        Assert.Equal(2, next.Count);
    }

    private static HostConnectionBudget Create(int connectionsPerHost, int transfersPerHost) =>
        new(Options.Create(new DownloadOptions
        {
            MaximumConnectionsPerHost = connectionsPerHost,
            MaximumPerHost = transfersPerHost,
        }));
}

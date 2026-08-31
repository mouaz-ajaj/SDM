using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace SDM.Application.Downloads;

public sealed class HostConnectionBudget : IConnectionBudget, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _connectionsPerHost;
    private readonly int _ceilingPerTransfer;

    public HostConnectionBudget(IOptions<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _connectionsPerHost = options.Value.MaximumConnectionsPerHost;

        // No single transfer may take the host's whole budget. One connection is held
        // back for each other transfer the host is allowed to run, otherwise a second
        // download of the same site would wait for the first to finish entirely rather
        // than merely sharing the bandwidth with it.
        _ceilingPerTransfer = Math.Max(1, _connectionsPerHost - options.Value.MaximumPerHost + 1);
    }

    public async Task<IConnectionLease> AcquireAsync(
        string host, int desired, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim slots = _hosts.GetOrAdd(
            host ?? string.Empty, _ => new SemaphoreSlim(_connectionsPerHost, _connectionsPerHost));

        int wanted = Math.Clamp(desired, 1, _ceilingPerTransfer);

        // The first connection is worth waiting for: without it there is no transfer.
        await slots.WaitAsync(cancellationToken);
        int granted = 1;

        // The rest are opportunistic. Waiting for them would let one large transfer
        // block a small one behind it for no benefit.
        while (granted < wanted && slots.Wait(0, CancellationToken.None))
        {
            granted++;
        }

        return new Lease(slots, granted);
    }

    public void Dispose()
    {
        foreach (SemaphoreSlim slots in _hosts.Values)
        {
            slots.Dispose();
        }

        _hosts.Clear();
    }

    private sealed class Lease(SemaphoreSlim slots, int count) : IConnectionLease
    {
        private bool _released;

        public int Count { get; } = count;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            slots.Release(Count);
        }
    }
}

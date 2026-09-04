using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public sealed class DownloadScheduler : IDownloadScheduler, IDisposable
{
    private readonly IStartDownloadUseCase _startDownload;
    private readonly SemaphoreSlim _globalSlots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maximumPerHost;

    public DownloadScheduler(IStartDownloadUseCase startDownload, IOptions<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(startDownload);
        ArgumentNullException.ThrowIfNull(options);

        _startDownload = startDownload;
        _globalSlots = new SemaphoreSlim(options.Value.MaximumConcurrent, options.Value.MaximumConcurrent);
        _maximumPerHost = options.Value.MaximumPerHost;
    }

    public async Task<DownloadResult> EnqueueAsync(
        string address,
        DownloadCallbacks? callbacks = null,
        DownloadDestination? destination = null,
        RequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim host = HostSlotsFor(address);

        // The host slot is taken first on purpose. Taking the global slot first would let
        // three transfers queued behind one busy server occupy every global slot and stall
        // downloads from other hosts that could have run immediately.
        await host.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _globalSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // Only now is the transfer really beginning; the caller has been showing it
                // as queued until this point.
                callbacks?.Started?.Invoke();
                return await _startDownload.ExecuteAsync(address, callbacks, destination, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _globalSlots.Release();
            }
        }
        finally
        {
            host.Release();
        }
    }

    public Task<DownloadProbe> ProbeAsync(string address, CancellationToken cancellationToken = default) =>
        _startDownload.ProbeAsync(address, cancellationToken);

    public void Discard(string destinationPath) => _startDownload.Discard(destinationPath);

    private SemaphoreSlim HostSlotsFor(string address)
    {
        string key = Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? source)
            ? source.Host
            : string.Empty;

        return _hostSlots.GetOrAdd(key, _ => new SemaphoreSlim(_maximumPerHost, _maximumPerHost));
    }

    public void Dispose()
    {
        _globalSlots.Dispose();

        foreach (SemaphoreSlim host in _hostSlots.Values)
        {
            host.Dispose();
        }

        _hostSlots.Clear();
    }
}

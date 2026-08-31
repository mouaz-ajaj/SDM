using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public sealed class DownloadScheduler : IDownloadScheduler, IDisposable
{
    private readonly IStartDownloadUseCase _startDownload;
    private readonly SemaphoreSlim _slots;

    public DownloadScheduler(IStartDownloadUseCase startDownload, IOptions<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(startDownload);
        ArgumentNullException.ThrowIfNull(options);

        _startDownload = startDownload;
        _slots = new SemaphoreSlim(options.Value.MaximumConcurrent, options.Value.MaximumConcurrent);
    }

    public async Task<DownloadResult> EnqueueAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        Action? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        await _slots.WaitAsync(cancellationToken);

        try
        {
            // Only now is the transfer really beginning; the caller has been showing it
            // as queued until this point.
            onStarted?.Invoke();
            return await _startDownload.ExecuteAsync(address, progress, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}

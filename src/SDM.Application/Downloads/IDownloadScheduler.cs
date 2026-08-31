using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public interface IDownloadScheduler
{
    /// <summary>
    /// Waits for a free slot — both globally and for the target host — then runs the
    /// transfer. Cancelling while still queued never starts it at all.
    /// </summary>
    Task<DownloadResult> EnqueueAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        Action? onStarted = null,
        Action<DownloadRetry>? onRetry = null,
        CancellationToken cancellationToken = default);
}

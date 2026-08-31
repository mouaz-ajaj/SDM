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
        DownloadCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);

    /// <summary>Throws away the partial file for a transfer the user has abandoned.</summary>
    void Discard(string destinationPath);
}

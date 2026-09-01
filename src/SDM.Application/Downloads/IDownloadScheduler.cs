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
        DownloadDestination? destination = null,
        RequestContext? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the server what a URL is without downloading it. Takes no slot: it is a
    /// single small request made before the user has even decided to keep the file.
    /// </summary>
    Task<DownloadProbe> ProbeAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Throws away the partial file for a transfer the user has abandoned.</summary>
    void Discard(string destinationPath);
}

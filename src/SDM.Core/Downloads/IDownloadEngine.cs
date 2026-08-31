namespace SDM.Core.Downloads;

public interface IDownloadEngine
{
    /// <summary>
    /// Transfers <paramref name="request"/> into its destination folder, continuing from
    /// a matching partial file when one exists. Cancellation and failure both keep the
    /// partial file so the transfer can be resumed; use <see cref="DiscardPartial"/> to
    /// throw it away.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        DownloadCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the server what a URL is — name, size, type — without downloading the body.
    /// </summary>
    Task<DownloadProbe> ProbeAsync(Uri source, CancellationToken cancellationToken = default);

    /// <summary>Deletes the partial file and its metadata for a destination that will not be resumed.</summary>
    void DiscardPartial(string destinationPath);
}

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
    /// <param name="context">
    /// The browser session the URL belongs to, when it has one. A protected file answers
    /// a bare probe with a sign-in page, whose name and size describe the wrong thing.
    /// </param>
    Task<DownloadProbe> ProbeAsync(
        Uri source, RequestContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes the partial file and its metadata for a destination that will not be resumed.</summary>
    void DiscardPartial(string destinationPath);
}

namespace SDM.Core.Downloads;

public interface IDownloadEngine
{
    /// <summary>
    /// Transfers <paramref name="request"/> to its destination and completes when the
    /// file is fully written. Cancellation leaves no file at the destination path.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

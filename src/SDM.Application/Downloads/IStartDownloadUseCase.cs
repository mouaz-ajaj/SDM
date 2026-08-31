using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public interface IStartDownloadUseCase
{
    /// <summary>
    /// Validates a user-entered address and transfers it into the download folder,
    /// retrying transient failures that happened before any bytes arrived.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not a usable HTTP or HTTPS URL.</exception>
    /// <exception cref="DownloadFailedException">The transfer failed and is not retryable.</exception>
    Task<DownloadResult> ExecuteAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        Action<DownloadRetry>? onRetry = null,
        CancellationToken cancellationToken = default);
}

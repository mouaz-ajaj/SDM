using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public interface IStartDownloadUseCase
{
    /// <summary>
    /// Validates a user-entered address and transfers it into the download folder.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not a usable HTTP or HTTPS URL.</exception>
    Task<DownloadResult> ExecuteAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

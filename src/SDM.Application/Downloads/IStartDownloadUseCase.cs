using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public interface IStartDownloadUseCase
{
    /// <summary>
    /// Validates a user-entered address and transfers it into the download folder,
    /// retrying transient failures. Because the engine resumes, a retry continues from
    /// where the previous attempt stopped rather than starting again.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not a usable HTTP or HTTPS URL.</exception>
    /// <exception cref="DownloadFailedException">The transfer failed and is not retryable.</exception>
    Task<DownloadResult> ExecuteAsync(
        string address,
        DownloadCallbacks? callbacks = null,
        CancellationToken cancellationToken = default);

    /// <summary>Throws away the partial file for a transfer the user has abandoned.</summary>
    void Discard(string destinationPath);
}

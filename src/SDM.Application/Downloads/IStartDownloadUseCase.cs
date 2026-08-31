using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public interface IStartDownloadUseCase
{
    /// <summary>
    /// Validates a user-entered address and transfers it, retrying transient failures.
    /// Because the engine resumes, a retry continues from where the previous attempt
    /// stopped rather than starting again.
    /// </summary>
    /// <param name="destination">
    /// A folder and name the user chose explicitly. Null lets the download folder and
    /// the category rules decide.
    /// </param>
    /// <exception cref="ArgumentException">The address is not a usable HTTP or HTTPS URL.</exception>
    /// <exception cref="DownloadFailedException">The transfer failed and is not retryable.</exception>
    Task<DownloadResult> ExecuteAsync(
        string address,
        DownloadCallbacks? callbacks = null,
        DownloadDestination? destination = null,
        CancellationToken cancellationToken = default);

    /// <summary>Asks the server what a URL is, so a save dialog can show its real name.</summary>
    Task<DownloadProbe> ProbeAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Throws away the partial file for a transfer the user has abandoned.</summary>
    void Discard(string destinationPath);
}

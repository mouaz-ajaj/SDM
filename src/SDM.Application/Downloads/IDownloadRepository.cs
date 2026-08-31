using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

/// <summary>
/// Remembers transfers between runs. Implemented by the database layer; the application
/// and user interface never open a connection themselves.
/// </summary>
public interface IDownloadRepository
{
    /// <summary>Newest first. Creates the store on first use.</summary>
    Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or updates one job.</summary>
    Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

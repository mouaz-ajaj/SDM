namespace SDM.Core.Downloads;

public interface IDownloadEngine
{
    Task StartAsync(DownloadRequest request, CancellationToken cancellationToken = default);
}

using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public sealed class StartDownloadUseCase : IStartDownloadUseCase
{
    private readonly IDownloadEngine _engine;
    private readonly IDownloadFolder _downloadFolder;

    public StartDownloadUseCase(IDownloadEngine engine, IDownloadFolder downloadFolder)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(downloadFolder);

        _engine = engine;
        _downloadFolder = downloadFolder;
    }

    public Task<DownloadResult> ExecuteAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? source))
        {
            throw new ArgumentException(
                "Enter a complete address, including http:// or https://.", nameof(address));
        }

        // DownloadRequest rejects anything that is not HTTP or HTTPS, with a message the
        // user interface can show as-is.
        DownloadRequest request = new(source, _downloadFolder.GetPath());

        return _engine.DownloadAsync(request, progress, cancellationToken);
    }
}

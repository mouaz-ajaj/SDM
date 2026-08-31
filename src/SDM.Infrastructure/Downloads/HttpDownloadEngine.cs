using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// Streams an HTTP(S) response straight to disk. The transfer is written to a
/// sibling <c>.part</c> file and moved into place only once it completes, so a
/// cancelled or failed download never leaves a truncated file at the destination.
/// </summary>
public sealed class HttpDownloadEngine : IDownloadEngine
{
    public const string HttpClientName = "SDM.Downloads";

    private const int BufferSize = 81920;
    private const string PartialSuffix = ".part";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDownloadEngine> _logger;

    public HttpDownloadEngine(IHttpClientFactory httpClientFactory, ILogger<HttpDownloadEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string destination = Path.GetFullPath(request.DestinationPath);
        string partialPath = destination + PartialSuffix;

        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using HttpResponseMessage response = await client
            .GetAsync(request.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        _logger.LogInformation(
            "Downloading {Source} to {Destination}; advertised length {TotalBytes}.",
            request.Source,
            destination,
            totalBytes);

        long bytesWritten;

        try
        {
            bytesWritten = await CopyToPartialFileAsync(
                response, partialPath, totalBytes, progress, cancellationToken);

            File.Move(partialPath, destination, overwrite: true);
        }
        catch
        {
            TryDeletePartialFile(partialPath);
            throw;
        }

        progress?.Report(new DownloadProgress(bytesWritten, totalBytes ?? bytesWritten));
        _logger.LogInformation("Completed {Destination}; wrote {BytesWritten} bytes.", destination, bytesWritten);

        return new DownloadResult(destination, bytesWritten);
    }

    private static async Task<long> CopyToPartialFileAsync(
        HttpResponseMessage response,
        string partialPath,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(
            partialPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long bytesWritten = 0;
        bool hasReported = false;
        Stopwatch sinceLastReport = Stopwatch.StartNew();

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            bytesWritten += read;

            // Report the first chunk immediately so the UI reacts at once, then throttle;
            // an unthrottled report per 80 KB read would flood the dispatcher on large files.
            if (!hasReported || sinceLastReport.Elapsed >= ProgressInterval)
            {
                progress?.Report(new DownloadProgress(bytesWritten, totalBytes));
                hasReported = true;
                sinceLastReport.Restart();
            }
        }

        await target.FlushAsync(cancellationToken);
        return bytesWritten;
    }

    private void TryDeletePartialFile(string partialPath)
    {
        try
        {
            File.Delete(partialPath);
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Could not remove the partial file {PartialPath}.", partialPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Could not remove the partial file {PartialPath}.", partialPath);
        }
    }
}

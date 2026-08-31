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
    private const int MaximumNameAttempts = 1000;
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

        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using HttpResponseMessage response = await client
            .GetAsync(request.Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        Directory.CreateDirectory(request.DestinationDirectory);

        string destination = ResolveDestinationPath(request, response);
        string partialPath = destination + PartialSuffix;
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

            File.Move(partialPath, destination, overwrite: false);
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

    /// <summary>
    /// Settles the file name from the caller's preference, then the server's suggestion,
    /// then the URL — and guarantees the result stays inside the destination directory
    /// and does not overwrite an existing file.
    /// </summary>
    private static string ResolveDestinationPath(DownloadRequest request, HttpResponseMessage response)
    {
        string? suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        string fileName = request.PreferredFileName ?? SafeFileName.Resolve(suggested, request.Source);
        string candidate = Path.GetFullPath(Path.Combine(request.DestinationDirectory, fileName));

        // SafeFileName already strips separators, so this can only fail if that contract
        // is ever broken. Checking anyway keeps a future regression from writing outside
        // the download folder.
        string root = request.DestinationDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(request.DestinationDirectory, SafeFileName.Fallback);
        }

        return EnsureUnique(candidate);
    }

    private static string EnsureUnique(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + PartialSuffix))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int attempt = 1; attempt <= MaximumNameAttempts; attempt++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({attempt}){extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + PartialSuffix))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
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

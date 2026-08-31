using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
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

    /// <summary>
    /// Status codes that mean "not now" rather than "not ever". 500 is deliberately absent:
    /// a generic server error is usually a real fault, and retrying it just adds load.
    /// </summary>
    private static readonly HashSet<HttpStatusCode> TransientStatusCodes =
    [
        HttpStatusCode.RequestTimeout,
        HttpStatusCode.TooManyRequests,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout,
    ];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpDownloadEngine> _logger;
    private readonly TimeSpan _idleTimeout;

    public HttpDownloadEngine(
        IHttpClientFactory httpClientFactory,
        IOptions<DownloadOptions> options,
        ILogger<HttpDownloadEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _idleTimeout = TimeSpan.FromSeconds(options.Value.IdleTimeoutSeconds);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One idle clock covers the whole transfer and is pushed forward on every chunk
        // that arrives, so a server that goes silent fails instead of hanging for ever.
        using CancellationTokenSource idle = new(_idleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        using HttpResponseMessage response = await SendAsync(request, linked.Token, cancellationToken);

        EnsureSuccess(response);

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
                response, partialPath, totalBytes, progress, idle, linked.Token, cancellationToken);

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

    private async Task<HttpResponseMessage> SendAsync(
        DownloadRequest request,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            return await client.GetAsync(request.Source, HttpCompletionOption.ResponseHeadersRead, linkedToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new DownloadFailedException(
                $"The server did not respond within {_idleTimeout.TotalSeconds:0} seconds.",
                statusCode: null,
                retryAfter: null,
                isTransient: true);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadFailedException(
                "Could not reach the server.", statusCode: null, retryAfter: null, isTransient: true, exception);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);

        throw new DownloadFailedException(
            $"Server answered {(int)response.StatusCode} {response.StatusCode}",
            (int)response.StatusCode,
            retryAfter > TimeSpan.Zero ? retryAfter : null,
            TransientStatusCodes.Contains(response.StatusCode));
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

    private async Task<long> CopyToPartialFileAsync(
        HttpResponseMessage response,
        string partialPath,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(linkedToken);
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

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, linkedToken)) > 0)
            {
                idle.CancelAfter(_idleTimeout);

                await target.WriteAsync(buffer.AsMemory(0, read), linkedToken);
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
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new DownloadFailedException(
                $"The server stopped sending data for {_idleTimeout.TotalSeconds:0} seconds.",
                statusCode: null,
                retryAfter: null,
                isTransient: true);
        }
        catch (IOException exception)
        {
            throw new DownloadFailedException(
                $"The connection failed after {bytesWritten} bytes.",
                statusCode: null,
                retryAfter: null,
                isTransient: true,
                exception);
        }

        await target.FlushAsync(linkedToken);
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

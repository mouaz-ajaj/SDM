using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// Streams an HTTP(S) response to disk, continuing from a matching <c>.part</c> file when
/// one exists. The file is moved into place only once complete, so a cancelled or failed
/// transfer never leaves a truncated file at the destination — it leaves something the
/// next attempt can carry on from.
/// </summary>
public sealed class HttpDownloadEngine : IDownloadEngine
{
    public const string HttpClientName = "SDM.Downloads";

    private const int BufferSize = 81920;
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
        DownloadCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(request.DestinationDirectory);

        ResumablePartial? existing = PartialFile.FindFor(request.DestinationDirectory, request.Source);

        // One idle clock covers the whole transfer and is pushed forward on every chunk
        // that arrives, so a server that goes silent fails instead of hanging for ever.
        using CancellationTokenSource idle = new(_idleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        using HttpResponseMessage response = await SendAsync(request, existing, linked.Token, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing is not null)
        {
            // The partial no longer lines up with what the server has. Throw it away and
            // let the retry loop start cleanly rather than append to nonsense.
            _logger.LogWarning("Discarding a stale partial file for {Source}.", request.Source);
            PartialFile.Delete(existing.PartialPath);

            throw new DownloadFailedException(
                "The partially downloaded file no longer matches the server; starting again.",
                (int)response.StatusCode,
                retryAfter: null,
                isTransient: true);
        }

        EnsureSuccess(response);

        // Asking for a range and getting 200 means the server ignored it — either it does
        // not support ranges or If-Range detected that the file changed. Either way the
        // only safe move is to start the file over.
        bool resuming = existing is not null && response.StatusCode == HttpStatusCode.PartialContent;
        long resumeFrom = resuming ? existing!.Length : 0;

        string destination = resuming
            ? existing!.DestinationPath
            : ResolveDestinationPath(request, response);

        string partialPath = destination + PartialFile.PartialSuffix;
        long? totalBytes = ResolveTotalLength(response, resumeFrom);

        if (!resuming)
        {
            PartialFile.Write(partialPath, request.Source, totalBytes, ValidatorFor(response));
        }

        bool serverSupportsResume = resuming || response.Headers.AcceptRanges.Contains("bytes");

        callbacks?.Planned?.Invoke(
            new DownloadPlan(destination, totalBytes, resumeFrom, serverSupportsResume));

        _logger.LogInformation(
            "Downloading {Source} to {Destination}; total {TotalBytes}, resuming from {ResumeFrom}.",
            request.Source,
            destination,
            totalBytes,
            resumeFrom);

        long bytesWritten = await CopyToPartialFileAsync(
            response, partialPath, resumeFrom, totalBytes, callbacks?.Progress, idle, linked.Token, cancellationToken);

        File.Move(partialPath, destination, overwrite: false);
        PartialFile.Delete(partialPath);

        callbacks?.Progress?.Report(new DownloadProgress(bytesWritten, totalBytes ?? bytesWritten));
        _logger.LogInformation("Completed {Destination}; {BytesWritten} bytes on disk.", destination, bytesWritten);

        return new DownloadResult(destination, bytesWritten);
    }

    public void DiscardPartial(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PartialFile.Delete(destinationPath + PartialFile.PartialSuffix);
    }

    private async Task<HttpResponseMessage> SendAsync(
        DownloadRequest request,
        ResumablePartial? existing,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using HttpRequestMessage message = new(HttpMethod.Get, request.Source);

        if (existing is not null)
        {
            message.Headers.Range = new RangeHeaderValue(existing.Length, null);

            // If-Range lets the server itself decide: it answers 206 when the resource is
            // unchanged and 200 when it is not, which is safer than trusting our own copy.
            if (existing.Metadata.Validator is { } validator)
            {
                message.Headers.TryAddWithoutValidation("If-Range", validator);
            }
        }

        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linkedToken);
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
    /// The full size of the resource. On a 206 the body length is only the remaining part,
    /// so the total comes from Content-Range, falling back to what is already on disk plus
    /// what is still to come.
    /// </summary>
    private static long? ResolveTotalLength(HttpResponseMessage response, long resumeFrom)
    {
        if (resumeFrom <= 0)
        {
            return response.Content.Headers.ContentLength;
        }

        return response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is { } remaining ? resumeFrom + remaining : null);
    }

    private static string? ValidatorFor(HttpResponseMessage response) =>
        response.Headers.ETag?.ToString()
        ?? response.Content.Headers.LastModified?.ToString("R");

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
        if (!Taken(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        for (int attempt = 1; attempt <= MaximumNameAttempts; attempt++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({attempt}){extension}");

            if (!Taken(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    // A partial file counts as taken: it belongs to some other transfer that may resume.
    private static bool Taken(string path) =>
        File.Exists(path) || File.Exists(path + PartialFile.PartialSuffix);

    private async Task<long> CopyToPartialFileAsync(
        HttpResponseMessage response,
        string partialPath,
        long resumeFrom,
        long? totalBytes,
        IProgress<DownloadProgress>? progress,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(linkedToken);
        await using FileStream target = new(
            partialPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long bytesWritten = resumeFrom;
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
}

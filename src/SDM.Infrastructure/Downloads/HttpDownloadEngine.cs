using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// Streams an HTTP(S) response to disk, splitting it across several connections when the
/// server supports byte ranges and continuing from a matching <c>.part</c> file when one
/// exists. The file is moved into place only once complete, so a cancelled or failed
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
    private readonly IConnectionBudget _connectionBudget;
    private readonly ILogger<HttpDownloadEngine> _logger;
    private readonly DownloadOptions _options;
    private readonly TimeSpan _idleTimeout;

    public HttpDownloadEngine(
        IHttpClientFactory httpClientFactory,
        IConnectionBudget connectionBudget,
        IOptions<DownloadOptions> options,
        ILogger<HttpDownloadEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(connectionBudget);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _connectionBudget = connectionBudget;
        _logger = logger;
        _options = options.Value;
        _idleTimeout = TimeSpan.FromSeconds(_options.IdleTimeoutSeconds);
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        DownloadCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(request.DestinationDirectory);

        ResumablePartial? existing = PartialFile.FindFor(request.DestinationDirectory, request.Source);

        // One idle clock covers the whole transfer and is pushed forward whenever any
        // connection delivers, so a server that goes silent fails instead of hanging.
        using CancellationTokenSource idle = new(_idleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        using IConnectionLease lease = await _connectionBudget.AcquireAsync(
            request.Source.Host, _options.MaximumSegments, cancellationToken);

        return existing?.Metadata.Segments is { Length: > 0 }
            ? await ResumeSegmentedAsync(request, existing, callbacks, idle, linked.Token, cancellationToken)
            : await StartAsync(request, existing, lease, callbacks, idle, linked.Token, cancellationToken);
    }

    public void DiscardPartial(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        PartialFile.Delete(destinationPath + PartialFile.PartialSuffix);
    }

    /// <summary>
    /// Opens the transfer. The very first request asks for an open-ended range, which
    /// both probes for range support and, if granted, begins the first segment — so
    /// discovering that a file can be split costs no extra round trip.
    /// </summary>
    private async Task<DownloadResult> StartAsync(
        DownloadRequest request,
        ResumablePartial? existing,
        IConnectionLease lease,
        DownloadCallbacks? callbacks,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        long streamResumeFrom = existing?.Length ?? 0;

        using HttpResponseMessage response = await SendAsync(
            request.Source, streamResumeFrom, null, existing?.Metadata.Validator, linkedToken, callerToken);

        await HandleStaleRangeAsync(response, existing, request);
        EnsureSuccess(response);

        // Asking for a range and getting 200 means the server ignored it — either it does
        // not support ranges or If-Range detected that the file changed. Either way the
        // only safe move is to start the file over.
        bool ranged = response.StatusCode == HttpStatusCode.PartialContent;
        bool resuming = ranged && streamResumeFrom > 0;
        long resumeFrom = resuming ? streamResumeFrom : 0;

        string destination = resuming && existing is not null
            ? existing.DestinationPath
            : ResolveDestinationPath(request, response);

        string partialPath = destination + PartialFile.PartialSuffix;
        long? totalBytes = ResolveTotalLength(response, resumeFrom);

        int segmentCount = ChooseSegmentCount(ranged, resuming, totalBytes, lease.Count);

        PartialFileMetadata metadata = new(
            request.Source.AbsoluteUri, totalBytes, ValidatorFor(response), null);

        _logger.LogInformation(
            "Downloading {Source} to {Destination}; total {TotalBytes}, resuming from {ResumeFrom}, {Segments} connection(s).",
            request.Source,
            destination,
            totalBytes,
            resumeFrom,
            segmentCount);

        callbacks?.Planned?.Invoke(new DownloadPlan(destination, totalBytes, resumeFrom, ranged, segmentCount));

        long bytesWritten = segmentCount > 1
            ? await RunSegmentedAsync(
                request, partialPath, metadata, SegmentedTransfer.Split(totalBytes!.Value, segmentCount),
                response, totalBytes.Value, callbacks, idle, linkedToken, callerToken)
            : await RunSingleStreamAsync(
                response, partialPath, metadata, resumeFrom, totalBytes, callbacks, idle, linkedToken, callerToken);

        return Complete(destination, partialPath, bytesWritten, totalBytes, callbacks);
    }

    /// <summary>Picks up a split transfer, opening a fresh connection for each unfinished part.</summary>
    private async Task<DownloadResult> ResumeSegmentedAsync(
        DownloadRequest request,
        ResumablePartial existing,
        DownloadCallbacks? callbacks,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        SegmentState[] segments = existing.Metadata.Segments!;
        long totalBytes = existing.Metadata.TotalBytes ?? segments[^1].End + 1;
        long alreadyDone = segments.Sum(segment => segment.Completed);

        _logger.LogInformation(
            "Resuming {Source} across {Segments} parts; {Done} of {Total} bytes already on disk.",
            request.Source,
            segments.Length,
            alreadyDone,
            totalBytes);

        callbacks?.Planned?.Invoke(
            new DownloadPlan(existing.DestinationPath, totalBytes, alreadyDone, true, segments.Length));

        long bytesWritten = await RunSegmentedAsync(
            request, existing.PartialPath, existing.Metadata, segments,
            null, totalBytes, callbacks, idle, linkedToken, callerToken);

        return Complete(existing.DestinationPath, existing.PartialPath, bytesWritten, totalBytes, callbacks);
    }

    private async Task<long> RunSegmentedAsync(
        DownloadRequest request,
        string partialPath,
        PartialFileMetadata metadata,
        SegmentState[] segments,
        HttpResponseMessage? firstResponse,
        long totalBytes,
        DownloadCallbacks? callbacks,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        using SafeFileHandle handle = File.OpenHandle(
            partialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);

        // Reserving the full length up front means every segment can write straight to its
        // own offset without the file having to grow underneath the others.
        RandomAccess.SetLength(handle, totalBytes);

        PartialFile.Write(partialPath, metadata with { Segments = segments });

        SegmentedTransfer transfer = new(segments, partialPath, metadata, totalBytes, callbacks?.Progress);

        try
        {
            return await transfer.RunAsync(
                handle,
                firstResponse,
                (segment, token) => SendAsync(
                    request.Source, segment.Position, segment.End, metadata.Validator, token, callerToken),
                () => idle.CancelAfter(_idleTimeout),
                linkedToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw IdleFailure();
        }
        catch (IOException exception)
        {
            throw new DownloadFailedException(
                "The connection failed part way through.", null, null, isTransient: true, exception);
        }
    }

    private async Task<long> RunSingleStreamAsync(
        HttpResponseMessage response,
        string partialPath,
        PartialFileMetadata metadata,
        long resumeFrom,
        long? totalBytes,
        DownloadCallbacks? callbacks,
        CancellationTokenSource idle,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        PartialFile.Write(partialPath, metadata);

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
                    callbacks?.Progress?.Report(new DownloadProgress(bytesWritten, totalBytes));
                    hasReported = true;
                    sinceLastReport.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw IdleFailure();
        }
        catch (IOException exception)
        {
            throw new DownloadFailedException(
                $"The connection failed after {bytesWritten} bytes.", null, null, isTransient: true, exception);
        }

        await target.FlushAsync(linkedToken);
        return bytesWritten;
    }

    private DownloadResult Complete(
        string destination,
        string partialPath,
        long bytesWritten,
        long? totalBytes,
        DownloadCallbacks? callbacks)
    {
        File.Move(partialPath, destination, overwrite: false);
        PartialFile.Delete(partialPath);

        callbacks?.Progress?.Report(new DownloadProgress(bytesWritten, totalBytes ?? bytesWritten));
        _logger.LogInformation("Completed {Destination}; {BytesWritten} bytes on disk.", destination, bytesWritten);

        return new DownloadResult(destination, bytesWritten);
    }

    /// <summary>
    /// Splitting is only worth it, and only safe, when the server granted a range, the
    /// full size is known, the file is big enough to pay for the extra handshakes, and
    /// the host's connection budget had room.
    /// </summary>
    private int ChooseSegmentCount(bool ranged, bool resuming, long? totalBytes, int availableConnections)
    {
        if (!ranged || resuming || totalBytes is not { } total)
        {
            return 1;
        }

        if (total < _options.SegmentThresholdBytes)
        {
            return 1;
        }

        return Math.Clamp(Math.Min(availableConnections, _options.MaximumSegments), 1, 16);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        long from,
        long? to,
        string? validator,
        CancellationToken linkedToken,
        CancellationToken callerToken)
    {
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using HttpRequestMessage message = new(HttpMethod.Get, source);

        // An open-ended range on a fresh transfer is a free probe: a 206 proves the
        // server can be split across connections, a 200 proves it cannot.
        message.Headers.Range = new RangeHeaderValue(from, to);

        // If-Range lets the server itself decide: it answers 206 when the resource is
        // unchanged and 200 when it is not, which is safer than trusting our own copy.
        if (from > 0 && validator is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Range", validator);
        }

        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linkedToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new DownloadFailedException(
                $"The server did not respond within {_idleTimeout.TotalSeconds:0} seconds.",
                null, null, isTransient: true);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadFailedException(
                "Could not reach the server.", null, null, isTransient: true, exception);
        }
    }

    private Task HandleStaleRangeAsync(
        HttpResponseMessage response, ResumablePartial? existing, DownloadRequest request)
    {
        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable || existing is null)
        {
            return Task.CompletedTask;
        }

        // The partial no longer lines up with what the server has. Throw it away and let
        // the retry loop start cleanly rather than append to nonsense.
        _logger.LogWarning("Discarding a stale partial file for {Source}.", request.Source);
        PartialFile.Delete(existing.PartialPath);

        throw new DownloadFailedException(
            "The partially downloaded file no longer matches the server; starting again.",
            (int)response.StatusCode,
            retryAfter: null,
            isTransient: true);
    }

    private DownloadFailedException IdleFailure() => new(
        $"The server stopped sending data for {_idleTimeout.TotalSeconds:0} seconds.",
        null, null, isTransient: true);

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
    /// The full size of the resource. On a 206 the body length is only the requested part,
    /// so the total comes from Content-Range, falling back to what is already on disk plus
    /// what is still to come.
    /// </summary>
    private static long? ResolveTotalLength(HttpResponseMessage response, long resumeFrom)
    {
        return response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is { } length ? resumeFrom + length : null);
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
}

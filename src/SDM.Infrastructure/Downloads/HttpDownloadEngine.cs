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
    private readonly IDownloadLayout _layout;
    private readonly ILogger<HttpDownloadEngine> _logger;
    private readonly IOptionsMonitor<DownloadOptions> _options;

    public HttpDownloadEngine(
        IHttpClientFactory httpClientFactory,
        IConnectionBudget connectionBudget,
        IDownloadLayout layout,
        IOptionsMonitor<DownloadOptions> options,
        ILogger<HttpDownloadEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(connectionBudget);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _connectionBudget = connectionBudget;
        _layout = layout;
        _logger = logger;
        _options = options;
    }

    /// <summary>Read per transfer so a changed value applies to the next download.</summary>
    private TimeSpan IdleTimeout => TimeSpan.FromSeconds(_options.CurrentValue.IdleTimeoutSeconds);

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
        using CancellationTokenSource idle = new(IdleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        using IConnectionLease lease = await _connectionBudget.AcquireAsync(
            request.Source.Host, _options.CurrentValue.MaximumSegments, cancellationToken);

        return existing?.Metadata.Segments is { Length: > 0 }
            ? await ResumeSegmentedAsync(request, existing, callbacks, idle, linked.Token, cancellationToken)
            : await StartAsync(request, existing, lease, callbacks, idle, linked.Token, cancellationToken);
    }

    public async Task<DownloadProbe> ProbeAsync(
        Uri source, RequestContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using CancellationTokenSource idle = new(IdleTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);

        // A one-byte range is the cheapest question that still gets a full answer: the
        // name from Content-Disposition, the size from Content-Range, and proof of
        // range support from the status code itself.
        using HttpResponseMessage response = await SendAsync(
            source, 0, 0, validator: null, context, linked.Token, cancellationToken);

        EnsureSuccess(response);

        bool ranged = response.StatusCode == HttpStatusCode.PartialContent;
        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        string? suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        // The same name the transfer would settle on, so the save dialog offers
        // "photo.jpg" rather than a bare "photo" the user has to correct by hand.
        string fileName = AddExtensionIfMissing(SafeFileName.Resolve(suggested, source), mediaType);

        long? totalBytes = response.Content.Headers.ContentRange?.Length
            ?? (ranged ? null : response.Content.Headers.ContentLength);

        return new DownloadProbe(
            fileName, totalBytes, mediaType, FileCategories.Resolve(fileName, mediaType), ranged);
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
            request.Source, streamResumeFrom, null, existing?.Metadata.Validator, request.Context,
            linkedToken, callerToken);

        await HandleStaleRangeAsync(response, existing, request);
        EnsureSuccess(response);

        // Asking for a range and getting 200 means the server ignored it — either it does
        // not support ranges or If-Range detected that the file changed. Either way the
        // only safe move is to start the file over.
        bool ranged = response.StatusCode == HttpStatusCode.PartialContent;
        bool resuming = ranged && streamResumeFrom > 0;
        long resumeFrom = resuming ? streamResumeFrom : 0;

        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        string destination = resuming && existing is not null
            ? existing.DestinationPath
            : ResolveDestinationPath(request, response, mediaType);

        FileCategory category = FileCategories.Resolve(Path.GetFileName(destination), mediaType);
        string partialPath = destination + PartialFile.PartialSuffix;
        long? totalBytes = ResolveTotalLength(response, resumeFrom);

        int segmentCount = ChooseSegmentCount(ranged, resuming, totalBytes, lease.Count);

        PartialFileMetadata metadata = new(
            request.Source.AbsoluteUri, totalBytes, ValidatorFor(response), null);

        _logger.LogInformation(
            "Downloading {Source} to {Destination}; {MediaType}, total {TotalBytes}, resuming from {ResumeFrom}, {Segments} connection(s).",
            request.Source,
            destination,
            mediaType ?? "type unknown",
            totalBytes,
            resumeFrom,
            segmentCount);

        callbacks?.Planned?.Invoke(new DownloadPlan(destination, totalBytes, resumeFrom, ranged, segmentCount)
        {
            MediaType = mediaType,
            Category = category,
        });

        long bytesWritten = segmentCount > 1
            ? await RunSegmentedAsync(
                request, partialPath, metadata, SegmentedTransfer.Split(totalBytes!.Value, segmentCount),
                response, totalBytes.Value, callbacks, idle, linkedToken, callerToken)
            : await RunSingleStreamAsync(
                response, partialPath, metadata, resumeFrom, totalBytes, callbacks, idle, linkedToken, callerToken);

        return Complete(destination, partialPath, bytesWritten, totalBytes, mediaType, category, callbacks);
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
            new DownloadPlan(existing.DestinationPath, totalBytes, alreadyDone, true, segments.Length)
            {
                Category = FileCategories.Resolve(Path.GetFileName(existing.DestinationPath)),
            });

        long bytesWritten = await RunSegmentedAsync(
            request, existing.PartialPath, existing.Metadata, segments,
            null, totalBytes, callbacks, idle, linkedToken, callerToken);

        // A resumed transfer never re-reads the headers, so the category comes from the
        // name the first attempt already settled on.
        return Complete(
            existing.DestinationPath,
            existing.PartialPath,
            bytesWritten,
            totalBytes,
            mediaType: null,
            FileCategories.Resolve(Path.GetFileName(existing.DestinationPath)),
            callbacks);
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

        SegmentedTransfer transfer = new(
            segments, partialPath, metadata, totalBytes, callbacks?.Progress, callbacks?.Segments);

        try
        {
            return await transfer.RunAsync(
                handle,
                firstResponse,
                (segment, token) => SendAsync(
                    request.Source, segment.Position, segment.End, metadata.Validator, request.Context,
                    token, callerToken),
                () => idle.CancelAfter(IdleTimeout),
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
                idle.CancelAfter(IdleTimeout);

                await target.WriteAsync(buffer.AsMemory(0, read), linkedToken);
                bytesWritten += read;

                // Report the first chunk immediately so the UI reacts at once, then throttle;
                // an unthrottled report per 80 KB read would flood the dispatcher on large files.
                if (!hasReported || sinceLastReport.Elapsed >= ProgressInterval)
                {
                    callbacks?.Progress?.Report(new DownloadProgress(bytesWritten, totalBytes));

                    // One connection is still one segment. Reporting it the same way keeps
                    // the interface from needing a special case for unsplit transfers.
                    callbacks?.Segments?.Report(
                    [
                        new SegmentProgress(1, 0, (totalBytes ?? bytesWritten) - 1, bytesWritten),
                    ]);

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
        string? mediaType,
        FileCategory category,
        DownloadCallbacks? callbacks)
    {
        callbacks?.Verifying?.Invoke();
        Verify(partialPath, bytesWritten, totalBytes);

        File.Move(partialPath, destination, overwrite: true);
        PartialFile.Delete(partialPath);

        callbacks?.Progress?.Report(new DownloadProgress(bytesWritten, totalBytes ?? bytesWritten));
        _logger.LogInformation("Completed {Destination}; {BytesWritten} bytes on disk.", destination, bytesWritten);

        return new DownloadResult(destination, bytesWritten) { MediaType = mediaType, Category = category };
    }

    /// <summary>
    /// Checks the transfer before the partial file is promoted to the real name.
    ///
    /// HTTP carries no checksum for an arbitrary file, so there is nothing to compare the
    /// contents against — but the server did say how many bytes it was sending, and that
    /// much can be held to. A stream that ends early looks exactly like a stream that
    /// ended on time, so without this a truncated file was renamed, reported as finished
    /// and its partial deleted, and the first thing to notice would have been the user
    /// opening a broken archive.
    ///
    /// A short transfer is transient on purpose: the partial file survives, so the retry
    /// resumes from where it stopped rather than starting the download again.
    /// </summary>
    private static void Verify(string partialPath, long bytesWritten, long? totalBytes)
    {
        if (totalBytes is { } expected && bytesWritten != expected)
        {
            throw new DownloadFailedException(
                $"The server promised {expected} bytes and sent {bytesWritten}.",
                statusCode: null,
                retryAfter: null,
                isTransient: true);
        }

        // What was counted is not proof of what landed: a write can fail after the bytes
        // were read, and a segmented transfer writes at offsets rather than sequentially.
        long onDisk = new FileInfo(partialPath).Length;

        if (onDisk != bytesWritten)
        {
            throw new DownloadFailedException(
                $"{bytesWritten} bytes were received but the file holds {onDisk}.",
                statusCode: null,
                retryAfter: null,
                isTransient: true);
        }
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

        if (total < _options.CurrentValue.SegmentThresholdBytes)
        {
            return 1;
        }

        return Math.Clamp(Math.Min(availableConnections, _options.CurrentValue.MaximumSegments), 1, 16);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri source,
        long from,
        long? to,
        string? validator,
        RequestContext? context,
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

        Apply(context, message);

        try
        {
            return await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linkedToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new DownloadFailedException(
                $"The server did not respond within {IdleTimeout.TotalSeconds:0} seconds.",
                null, null, isTransient: true);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadFailedException(
                "Could not reach the server.", null, null, isTransient: true, exception);
        }
    }

    /// <summary>
    /// Carries the browser's session onto the request. Added without validation on
    /// purpose: these values are the browser's own, and .NET rejects header shapes that
    /// real servers accept and real browsers send. Every one is applied per request rather
    /// than on the shared HttpClient, because one transfer's cookies must never leak onto
    /// another's — the client is reused across every download in the application.
    /// </summary>
    private static void Apply(RequestContext? context, HttpRequestMessage message)
    {
        if (context is null)
        {
            return;
        }

        if (context.Headers is { Count: > 0 })
        {
            foreach ((string name, string value) in context.Headers)
            {
                if (IsOursToDecide(name))
                {
                    continue;
                }

                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        // Only where the captured set did not already carry them, or the value would be
        // sent twice. These remain for the right-click path, where there is no request to
        // copy — the user picked a link the browser was never asked to fetch.
        AddIfAbsent(message, "Cookie", context.Cookie);
        AddIfAbsent(message, "Referer", context.Referrer);
        AddIfAbsent(message, "User-Agent", context.UserAgent);
    }

    private static void AddIfAbsent(HttpRequestMessage message, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !message.Headers.Contains(name))
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Headers the transfer owns and a copied request must not override.
    ///
    /// <c>Range</c> and <c>If-Range</c> are how a transfer is split and resumed; taking the
    /// browser's would ask for the wrong bytes. <c>Accept-Encoding</c> is the dangerous one:
    /// the engine turns automatic decompression off so that what is counted is what lands on
    /// disk, so inviting a compressed response would write gzip into a file named .zip and
    /// call it finished. The rest are per-connection details that belong to whoever is
    /// making the connection.
    /// </summary>
    private static bool IsOursToDecide(string name) =>
        name.Equals("Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("If-Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);

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
        $"The server stopped sending data for {IdleTimeout.TotalSeconds:0} seconds.",
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
    private string ResolveDestinationPath(
        DownloadRequest request, HttpResponseMessage response, string? mediaType)
    {
        string? suggested = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        string fileName = AddExtensionIfMissing(
            request.PreferredFileName ?? SafeFileName.Resolve(suggested, request.Source), mediaType);

        // Only now, with both the settled name and the server's type in hand, can the
        // category folder be chosen — unless the user picked a folder in a save dialog,
        // in which case sorting would move the file away from where they put it.
        string directory = request.ChosenByUser
            ? request.DestinationDirectory
            : _layout.ResolveDirectory(request.DestinationDirectory, fileName, mediaType);

        Directory.CreateDirectory(directory);

        string candidate = Path.GetFullPath(Path.Combine(directory, fileName));

        // SafeFileName already strips separators, so this can only fail if that contract
        // is ever broken. Checking anyway keeps a future regression from writing outside
        // the download folder.
        string root = request.DestinationDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.Combine(request.DestinationDirectory, SafeFileName.Fallback);
        }

        // The system save dialog has already asked about replacing an existing file, so
        // second-guessing it with "name (1)" would ignore what the user just said.
        return request.ChosenByUser ? candidate : EnsureUnique(candidate);
    }

    /// <summary>
    /// Gives a nameless file the extension its type implies. A URL ending in an opaque id
    /// yields a name like "images", and Windows has no way to open, preview or associate a
    /// file with no extension — while the server had already said it was a JPEG.
    ///
    /// A name that already has an extension is never touched. Servers mislabel Content-Type
    /// far more often than they mislabel names, and a .zip served as octet-stream must stay
    /// a .zip.
    /// </summary>
    private static string AddExtensionIfMissing(string fileName, string? mediaType)
    {
        if (Path.GetExtension(fileName).Length > 1)
        {
            return fileName;
        }

        return MediaTypeExtensions.ForMediaType(mediaType) is { } extension
            ? fileName + extension
            : fileName;
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

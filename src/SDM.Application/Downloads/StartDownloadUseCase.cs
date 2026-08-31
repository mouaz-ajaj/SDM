using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public sealed class StartDownloadUseCase : IStartDownloadUseCase
{
    private readonly IDownloadEngine _engine;
    private readonly IDownloadFolder _downloadFolder;
    private readonly DownloadOptions _options;

    public StartDownloadUseCase(
        IDownloadEngine engine,
        IDownloadFolder downloadFolder,
        IOptions<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _downloadFolder = downloadFolder;
        _options = options.Value;
    }

    public async Task<DownloadResult> ExecuteAsync(
        string address,
        IProgress<DownloadProgress>? progress = null,
        Action<DownloadRetry>? onRetry = null,
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

        for (int attempt = 1; ; attempt++)
        {
            FirstByteWatcher watcher = new(progress);

            try
            {
                return await _engine.DownloadAsync(request, watcher, cancellationToken);
            }
            catch (DownloadFailedException failure)
                when (ShouldRetry(failure, attempt, watcher.ReceivedBytes))
            {
                TimeSpan delay = DelayFor(failure, attempt);
                onRetry?.Invoke(new DownloadRetry(attempt, _options.MaximumAttempts, delay, failure.Message));

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Retrying is only worthwhile while nothing has been transferred. Until the engine
    /// can resume (Phase 3.1), a retry after 900 MB would throw those 900 MB away and
    /// start again — turning one failure into repeated waste of the user's bandwidth.
    /// </summary>
    private bool ShouldRetry(DownloadFailedException failure, int attempt, bool receivedBytes) =>
        failure.IsTransient && attempt < _options.MaximumAttempts && !receivedBytes;

    private TimeSpan DelayFor(DownloadFailedException failure, int attempt)
    {
        TimeSpan cap = TimeSpan.FromSeconds(_options.MaximumRetryDelaySeconds);

        // The server's own instruction wins over guesswork, but it does not get to park
        // the download for an hour.
        if (failure.RetryAfter is { } requested)
        {
            return requested < cap ? requested : cap;
        }

        // Exponential backoff with jitter, so several rows rejected together do not all
        // come back at the same instant and trip the same limit again.
        double seconds = Math.Pow(2, attempt - 1) + Random.Shared.NextDouble();
        TimeSpan backoff = TimeSpan.FromSeconds(seconds);

        return backoff < cap ? backoff : cap;
    }

    /// <summary>Passes progress through while recording whether anything actually arrived.</summary>
    private sealed class FirstByteWatcher(IProgress<DownloadProgress>? inner) : IProgress<DownloadProgress>
    {
        public bool ReceivedBytes { get; private set; }

        public void Report(DownloadProgress value)
        {
            if (value.BytesReceived > 0)
            {
                ReceivedBytes = true;
            }

            inner?.Report(value);
        }
    }
}

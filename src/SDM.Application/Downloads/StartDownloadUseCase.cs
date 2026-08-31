using Microsoft.Extensions.Options;
using SDM.Core.Downloads;

namespace SDM.Application.Downloads;

public sealed class StartDownloadUseCase : IStartDownloadUseCase
{
    private readonly IDownloadEngine _engine;
    private readonly IDownloadFolder _downloadFolder;
    private readonly IOptionsMonitor<DownloadOptions> _options;

    public StartDownloadUseCase(
        IDownloadEngine engine,
        IDownloadFolder downloadFolder,
        IOptionsMonitor<DownloadOptions> options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(options);

        _engine = engine;
        _downloadFolder = downloadFolder;
        _options = options;
    }

    public async Task<DownloadResult> ExecuteAsync(
        string address,
        DownloadCallbacks? callbacks = null,
        DownloadDestination? destination = null,
        CancellationToken cancellationToken = default)
    {
        Uri source = Parse(address);

        // DownloadRequest rejects anything that is not HTTP or HTTPS, with a message the
        // user interface can show as-is.
        DownloadRequest request = destination is null
            ? new DownloadRequest(source, _downloadFolder.GetPath())
            : new DownloadRequest(source, destination.Directory, destination.FileName, chosenByUser: true);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _engine.DownloadAsync(request, callbacks, cancellationToken);
            }
            catch (DownloadFailedException failure)
                when (failure.IsTransient && attempt < _options.CurrentValue.MaximumAttempts)
            {
                // Since Phase 3.1 the engine resumes from the partial file it left behind,
                // so a retry continues rather than discarding everything transferred so far.
                TimeSpan delay = DelayFor(failure, attempt);
                callbacks?.Retrying?.Invoke(
                    new DownloadRetry(attempt, _options.CurrentValue.MaximumAttempts, delay, failure.Message));

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public Task<DownloadProbe> ProbeAsync(string address, CancellationToken cancellationToken = default) =>
        _engine.ProbeAsync(Parse(address), cancellationToken);

    public void Discard(string destinationPath) => _engine.DiscardPartial(destinationPath);

    private static Uri Parse(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? source))
        {
            throw new ArgumentException(
                "Enter a complete address, including http:// or https://.", nameof(address));
        }

        return source;
    }

    private TimeSpan DelayFor(DownloadFailedException failure, int attempt)
    {
        TimeSpan cap = TimeSpan.FromSeconds(_options.CurrentValue.MaximumRetryDelaySeconds);

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
}

using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

/// <summary>
/// One row in the transfer list. Owns its own cancellation source, so pausing or
/// cancelling a row never touches its neighbours.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    // Speed measured over a single 100 ms progress tick swings wildly. Blending each
    // sample into a running average gives a figure that is readable rather than jittery.
    private const double SmoothingFactor = 0.3;
    private const double MinimumSampleSeconds = 0.15;

    private readonly IDownloadScheduler _scheduler;
    private readonly string _address;

    private CancellationTokenSource _cancellation = new();
    private long _lastBytes;
    private long _lastTimestamp;
    private double _bytesPerSecond;
    private bool _pauseRequested;
    private bool _disposed;

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentageText))]
    private double _percentage;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsResumable))]
    [NotifyPropertyChangedFor(nameof(ShowsProgress))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private DownloadStatus _status = DownloadStatus.Pending;

    [ObservableProperty]
    private string _detail = "Queued";

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <summary>Where the engine decided to write. Known once the response headers arrive.</summary>
    [ObservableProperty]
    private string? _destinationPath;

    [ObservableProperty]
    private bool _serverSupportsResume = true;

    public DownloadItemViewModel(IDownloadScheduler scheduler, string address)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        _scheduler = scheduler;
        _address = address.Trim();
        _fileName = PreviewFileName(_address);
    }

    public string Address => _address;

    public bool IsActive => Status is DownloadStatus.Pending or DownloadStatus.Running;

    public bool IsCompleted => Status is DownloadStatus.Completed;

    public bool IsPaused => Status is DownloadStatus.Paused;

    /// <summary>Paused and failed rows can both be picked up again from their partial file.</summary>
    public bool IsResumable => Status is DownloadStatus.Paused or DownloadStatus.Failed;

    public bool ShowsProgress => IsActive || IsPaused;

    public string PercentageText => Percentage.ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Runs the transfer to completion. Never throws: failures become status.</summary>
    public async Task RunAsync()
    {
        _pauseRequested = false;

        DownloadCallbacks callbacks = new()
        {
            Progress = new Progress<DownloadProgress>(OnProgress),
            Planned = OnPlanned,
            Retrying = OnRetry,
            Started = OnStarted,
        };

        try
        {
            DownloadResult result = await _scheduler.EnqueueAsync(_address, callbacks, _cancellation.Token);

            DestinationPath = result.DestinationPath;
            FileName = Path.GetFileName(result.DestinationPath);
            Percentage = 100;
            IsIndeterminate = false;
            Status = DownloadStatus.Completed;
            Detail = $"{FormatBytes(result.BytesWritten)} · {Path.GetDirectoryName(result.DestinationPath)}";
            SpeedText = string.Empty;
            RemainingText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Pause and cancel both cancel the token; only the row knows which was meant.
            if (_pauseRequested)
            {
                Settle(DownloadStatus.Paused, "Paused");
            }
            else
            {
                Settle(DownloadStatus.Cancelled, "Cancelled");
                DiscardPartial();
            }
        }
        catch (ArgumentException exception)
        {
            Settle(DownloadStatus.Failed, exception.Message);
        }
        catch (DownloadFailedException exception)
        {
            Settle(DownloadStatus.Failed, exception.Message);
        }
        catch (IOException exception)
        {
            // Reading the socket and writing the file both surface as IOException, so the
            // message must not claim to know which one failed.
            Settle(DownloadStatus.Failed, $"Transfer failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            Settle(DownloadStatus.Failed, "Access to the download folder was denied");
        }
    }

    /// <summary>Cancels and waits for the transfer to unwind. Used while the window closes.</summary>
    public async Task StopAndWaitAsync(bool keepPartialFile)
    {
        if (!IsActive)
        {
            return;
        }

        _pauseRequested = keepPartialFile;
        _cancellation.Cancel();

        for (int attempt = 0; attempt < 100 && IsActive; attempt++)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>Removes the partial file for a row the user is throwing away.</summary>
    public void DiscardPartial()
    {
        if (DestinationPath is { } path)
        {
            _scheduler.Discard(path);
        }
    }

    private bool CanCancel => IsActive || IsResumable;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        _pauseRequested = false;

        if (IsActive)
        {
            _cancellation.Cancel();
            return;
        }

        // Already stopped: there is nothing to interrupt, only a partial file to remove.
        Settle(DownloadStatus.Cancelled, "Cancelled");
        DiscardPartial();
    }

    private bool CanPause => IsActive && ServerSupportsResume;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _pauseRequested = true;
        _cancellation.Cancel();
    }

    private bool CanResume => IsResumable;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private async Task ResumeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _cancellation.Dispose();
        _cancellation = new CancellationTokenSource();

        Status = DownloadStatus.Pending;
        Detail = "Queued";
        IsIndeterminate = true;

        await RunAsync();
    }

    private void OnPlanned(DownloadPlan plan)
    {
        DestinationPath = plan.DestinationPath;
        FileName = Path.GetFileName(plan.DestinationPath);
        ServerSupportsResume = plan.ServerSupportsResume;
        PauseCommand.NotifyCanExecuteChanged();

        if (plan.ResumedFrom > 0)
        {
            Detail = $"Resuming from {FormatBytes(plan.ResumedFrom)}";
        }

        // Anchor the rate window at the resume point so the first sample is not a spike.
        _lastBytes = plan.ResumedFrom;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    private void OnRetry(DownloadRetry retry)
    {
        Status = DownloadStatus.Pending;
        SpeedText = string.Empty;
        RemainingText = string.Empty;
        Detail = $"{retry.Reason} — retrying in {retry.Delay.TotalSeconds:0}s "
            + $"(attempt {retry.Attempt} of {retry.MaximumAttempts})";
    }

    private void OnStarted()
    {
        Status = DownloadStatus.Running;
        Detail = "Connecting…";
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    private void OnProgress(DownloadProgress progress)
    {
        Status = DownloadStatus.Running;
        IsIndeterminate = progress.TotalBytes is null;
        Percentage = progress.Percentage ?? 0;

        Detail = progress.TotalBytes is { } total
            ? $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(total)}"
            : $"{FormatBytes(progress.BytesReceived)} · size unknown";

        UpdateRate(progress);
    }

    private void UpdateRate(DownloadProgress progress)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;

        if (seconds < MinimumSampleSeconds)
        {
            return;
        }

        double instant = (progress.BytesReceived - _lastBytes) / seconds;
        _bytesPerSecond = _bytesPerSecond <= 0
            ? instant
            : (_bytesPerSecond * (1 - SmoothingFactor)) + (instant * SmoothingFactor);

        _lastBytes = progress.BytesReceived;
        _lastTimestamp = now;

        SpeedText = _bytesPerSecond > 0 ? $"{FormatBytes((long)_bytesPerSecond)}/s" : string.Empty;
        RemainingText = progress.TotalBytes is { } total && _bytesPerSecond > 0
            ? FormatDuration(TimeSpan.FromSeconds((total - progress.BytesReceived) / _bytesPerSecond))
            : string.Empty;
    }

    private void Settle(DownloadStatus status, string detail)
    {
        Status = status;
        Detail = detail;
        IsIndeterminate = false;
        SpeedText = string.Empty;
        RemainingText = string.Empty;
        _bytesPerSecond = 0;
    }

    private static string PreviewFileName(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? source)
            ? SafeFileName.FromUri(source)
            : SafeFileName.Fallback;

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {ByteUnits[unit]}");
    }

    private static string FormatDuration(TimeSpan remaining)
    {
        if (remaining.TotalSeconds is <= 0 or > 86400)
        {
            return string.Empty;
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m left"
            : remaining.TotalMinutes >= 1
                ? $"{remaining.Minutes}m {remaining.Seconds}s left"
                : $"{remaining.Seconds}s left";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Dispose();
    }
}

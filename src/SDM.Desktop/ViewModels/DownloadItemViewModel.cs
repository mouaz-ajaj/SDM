using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

/// <summary>
/// One row in the transfer list. Owns its own cancellation source, so cancelling a row
/// never touches its neighbours.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    // Speed measured over a single 100 ms progress tick swings wildly. Blending each
    // sample into a running average gives a figure that is readable rather than jittery.
    private const double SmoothingFactor = 0.3;
    private const double MinimumSampleSeconds = 0.15;

    private readonly IDownloadScheduler _scheduler;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _address;

    private long _lastBytes;
    private long _lastTimestamp;
    private double _bytesPerSecond;
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
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private DownloadStatus _status = DownloadStatus.Pending;

    [ObservableProperty]
    private string _detail = "Queued";

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

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

    public bool IsFailed => Status is DownloadStatus.Failed;

    public string PercentageText => Percentage.ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Runs the transfer to completion. Never throws: failures become status.</summary>
    public async Task RunAsync()
    {
        Progress<DownloadProgress> progress = new(OnProgress);

        try
        {
            DownloadResult result = await _scheduler.EnqueueAsync(
                _address, progress, OnStarted, OnRetry, _cancellation.Token);

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
            Fail(DownloadStatus.Cancelled, "Cancelled");
        }
        catch (ArgumentException exception)
        {
            Fail(DownloadStatus.Failed, exception.Message);
        }
        catch (DownloadFailedException exception)
        {
            Fail(DownloadStatus.Failed, exception.Message);
        }
        catch (HttpRequestException exception)
        {
            Fail(DownloadStatus.Failed, exception.StatusCode is { } status
                ? $"Server answered {(int)status} {status}"
                : "Could not reach the server");
        }
        catch (IOException exception)
        {
            // Reading the socket and writing the file both surface as IOException, so the
            // message must not claim to know which one failed.
            Fail(DownloadStatus.Failed, $"Transfer failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            Fail(DownloadStatus.Failed, "Access to the download folder was denied");
        }
    }

    /// <summary>Cancels and waits for the engine to finish cleaning up its partial file.</summary>
    public async Task CancelAndWaitAsync()
    {
        if (!IsActive)
        {
            return;
        }

        CancelCommand.Execute(null);

        // Give the engine a moment to unwind and delete its .part file. Without this the
        // process can exit first and leave the partial file orphaned on disk.
        for (int attempt = 0; attempt < 50 && IsActive; attempt++)
        {
            await Task.Delay(20);
        }
    }

    private bool CanCancel => IsActive;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (!_disposed)
        {
            _cancellation.Cancel();
        }
    }

    private void OnRetry(DownloadRetry retry)
    {
        Status = DownloadStatus.Pending;
        IsIndeterminate = true;
        Percentage = 0;
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

    private void Fail(DownloadStatus status, string detail)
    {
        Status = status;
        Detail = detail;
        IsIndeterminate = false;
        SpeedText = string.Empty;
        RemainingText = string.Empty;
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

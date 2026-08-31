using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private readonly IApplicationInfoService _applicationInfo;
    private readonly IStartDownloadUseCase _startDownload;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private bool _hasTransfer;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _transferSummary = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public MainWindowViewModel(
        IApplicationInfoService applicationInfo,
        IStartDownloadUseCase startDownload,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(startDownload);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _startDownload = startDownload;
        _logger = logger;
    }

    public string Name => _applicationInfo.Name;

    public string FullName => _applicationInfo.FullName;

    public string Version => $"v{_applicationInfo.Version}";

    public string PercentageText => Percentage.ToString("0", CultureInfo.CurrentCulture) + "%";

    private bool CanDownload => !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        string requested = Address.Trim();

        ErrorMessage = null;
        HasTransfer = true;
        IsComplete = false;
        IsIndeterminate = true;
        Percentage = 0;
        FileName = PreviewFileName(requested);
        TransferSummary = "Connecting…";

        // Progress<T> captures this thread's synchronization context, so reports arrive
        // back on the UI thread and these property writes are safe.
        Progress<DownloadProgress> progress = new(OnProgress);

        try
        {
            DownloadResult result = await _startDownload.ExecuteAsync(requested, progress, cancellationToken);

            FileName = Path.GetFileName(result.DestinationPath);
            TransferSummary = $"{FormatBytes(result.BytesWritten)} · saved to {Path.GetDirectoryName(result.DestinationPath)}";
            Percentage = 100;
            IsIndeterminate = false;
            IsComplete = true;
            Address = string.Empty;
        }
        catch (OperationCanceledException)
        {
            HasTransfer = false;
        }
        catch (ArgumentException exception)
        {
            Fail(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            Fail(exception.StatusCode is { } status
                ? $"The server answered {(int)status} {status}."
                : "Could not reach the server. Check the address and your connection.");
        }
        catch (IOException exception)
        {
            Fail($"Could not write the file: {exception.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            Fail("Access to the download folder was denied.");
        }
        catch (Exception exception)
        {
            // A command handler is the last line of defence: an unhandled exception here
            // would take the whole window down.
            _logger.LogError(exception, "Unexpected failure downloading {Address}.", requested);
            Fail("The download failed unexpectedly. See the log for details.");
        }
    }

    private void OnProgress(DownloadProgress progress)
    {
        IsIndeterminate = progress.TotalBytes is null;
        Percentage = progress.Percentage ?? 0;

        TransferSummary = progress.TotalBytes is { } total
            ? $"{FormatBytes(progress.BytesReceived)} of {FormatBytes(total)}"
            : $"{FormatBytes(progress.BytesReceived)} · size unknown";
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        HasTransfer = false;
        IsIndeterminate = false;
        Percentage = 0;
    }

    /// <summary>
    /// A provisional name to show while connecting. The engine settles the real one from
    /// the response headers, and it replaces this once the transfer finishes.
    /// </summary>
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

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{value:0.#} {ByteUnits[unit]}");
    }

    partial void OnPercentageChanged(double value) => OnPropertyChanged(nameof(PercentageText));
}

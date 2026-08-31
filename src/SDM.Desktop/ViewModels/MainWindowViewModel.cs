using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationInfoService _applicationInfo;
    private readonly IDownloadScheduler _scheduler;
    private readonly IDownloadRepository _repository;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public MainWindowViewModel(
        IApplicationInfoService applicationInfo,
        IDownloadScheduler scheduler,
        IDownloadRepository repository,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _scheduler = scheduler;
        _repository = repository;
        _logger = logger;

        Downloads.CollectionChanged += OnDownloadsChanged;
    }

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    public string Name => _applicationInfo.Name;

    public string FullName => _applicationInfo.FullName;

    public string Version => $"v{_applicationInfo.Version}";

    public bool HasDownloads => Downloads.Count > 0;

    public bool HasActiveDownloads => Downloads.Any(download => download.IsActive);

    /// <summary>
    /// Restores the list saved by the previous run. Anything that was still transferring
    /// when the process ended is shown as paused — it plainly is not running now — with
    /// its partial file waiting for the resume button.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            foreach (DownloadJob job in await _repository.GetAllAsync())
            {
                Downloads.Add(DownloadItemViewModel.Restore(_scheduler, _repository, _logger, job));
            }

            _logger.LogInformation("Restored {Count} transfers from the previous session.", Downloads.Count);
        }
        catch (Exception exception)
        {
            // A broken database must not stop the application from starting.
            _logger.LogError(exception, "Could not restore the previous session's transfers.");
            ErrorMessage = "Previous downloads could not be restored.";
        }
    }

    private bool CanAdd => !string.IsNullOrWhiteSpace(Address);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        string address = Address.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = "Enter a complete http:// or https:// address.";
            return;
        }

        ErrorMessage = null;
        Address = string.Empty;

        DownloadItemViewModel item = DownloadItemViewModel.Create(_scheduler, _repository, _logger, address);
        Downloads.Insert(0, item);

        // RunAsync never throws — it turns every failure into the row's own status — so
        // there is no unobserved task to await here.
        _ = item.RunAsync();
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        foreach (DownloadItemViewModel finished in Downloads.Where(d => !d.IsActive).ToList())
        {
            // Removing a paused or failed row is the user abandoning it, so its partial
            // file goes too rather than lingering in the download folder for ever.
            if (!finished.IsCompleted)
            {
                finished.DiscardPartial();
            }

            Downloads.Remove(finished);
            await finished.ForgetAsync();
            finished.Dispose();
        }
    }

    /// <summary>
    /// Stops every running transfer and waits for it to unwind before the process exits.
    /// Called while the window is closing.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Closing is not abandoning: partial files are kept so the transfers can be
        // resumed the next time the application starts.
        await Task.WhenAll(Downloads.Select(download => download.StopAndWaitAsync(keepPartialFile: true)));

        foreach (DownloadItemViewModel download in Downloads)
        {
            download.Dispose();
        }
    }

    private void OnDownloadsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(HasActiveDownloads));
    }
}

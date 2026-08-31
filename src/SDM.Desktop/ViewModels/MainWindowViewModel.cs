using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Core.Downloads;
using SDM.Desktop.Services;

namespace SDM.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationInfoService _applicationInfo;
    private readonly IDownloadScheduler _scheduler;
    private readonly IDownloadRepository _repository;
    private readonly IDownloadFolder _downloadFolder;
    private readonly ISaveLocationPicker _picker;
    private readonly DownloadOptions _options;
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
        IDownloadFolder downloadFolder,
        ISaveLocationPicker picker,
        IOptions<DownloadOptions> options,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _scheduler = scheduler;
        _repository = repository;
        _downloadFolder = downloadFolder;
        _picker = picker;
        _options = options.Value;
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
    private async Task AddAsync()
    {
        string address = Address.Trim();

        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            ErrorMessage = "Enter a complete http:// or https:// address.";
            return;
        }

        ErrorMessage = null;
        DownloadDestination? destination = null;

        if (_options.AskWhereToSave)
        {
            destination = await ChooseDestinationAsync(address, source);

            // A dismissed dialog means the user changed their mind. The address stays in
            // the box so they can adjust it rather than paste it again.
            if (destination is null)
            {
                return;
            }
        }

        Address = string.Empty;

        DownloadItemViewModel item = DownloadItemViewModel.Create(
            _scheduler, _repository, _logger, address, destination);

        Downloads.Insert(0, item);

        // RunAsync never throws — it turns every failure into the row's own status — so
        // there is no unobserved task to await here.
        _ = item.RunAsync();
    }

    /// <summary>
    /// Asks the server for the real file name before opening the dialog. Guessing it from
    /// the URL would offer "download" for every link that ends in an opaque id.
    /// </summary>
    private async Task<DownloadDestination?> ChooseDestinationAsync(string address, Uri source)
    {
        string suggested;

        try
        {
            DownloadProbe probe = await _scheduler.ProbeAsync(address);
            suggested = probe.FileName;
        }
        catch (Exception exception) when (exception is DownloadFailedException or ArgumentException)
        {
            // Worth showing the dialog anyway: the transfer will ask the server again,
            // and a name from the URL is better than refusing to start.
            _logger.LogWarning(exception, "Could not look up {Address} before saving.", address);
            suggested = SafeFileName.FromUri(source);
        }

        return await _picker.PickAsync(suggested, _downloadFolder.GetPath());
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

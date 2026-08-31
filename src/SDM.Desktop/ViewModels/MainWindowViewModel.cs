using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;

namespace SDM.Desktop.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IApplicationInfoService _applicationInfo;
    private readonly IDownloadScheduler _scheduler;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public MainWindowViewModel(IApplicationInfoService applicationInfo, IDownloadScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(scheduler);

        _applicationInfo = applicationInfo;
        _scheduler = scheduler;

        Downloads.CollectionChanged += OnDownloadsChanged;
    }

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    public string Name => _applicationInfo.Name;

    public string FullName => _applicationInfo.FullName;

    public string Version => $"v{_applicationInfo.Version}";

    public bool HasDownloads => Downloads.Count > 0;

    public bool HasActiveDownloads => Downloads.Any(download => download.IsActive);

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

        DownloadItemViewModel item = new(_scheduler, address);
        Downloads.Insert(0, item);

        // RunAsync never throws — it turns every failure into the row's own status — so
        // there is no unobserved task to await here.
        _ = item.RunAsync();
    }

    [RelayCommand]
    private void ClearFinished()
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
            finished.Dispose();
        }
    }

    /// <summary>
    /// Stops every running transfer and waits for it to unwind before the process exits.
    /// Called while the window is closing, before the process is allowed to exit.
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

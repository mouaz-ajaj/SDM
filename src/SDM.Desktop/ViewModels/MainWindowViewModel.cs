using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
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
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private static readonly FileCategory[] SidebarCategories =
    [
        FileCategory.Programs,
        FileCategory.Compressed,
        FileCategory.Documents,
        FileCategory.Video,
        FileCategory.Audio,
        FileCategory.Images,
    ];

    private readonly IApplicationInfoService _applicationInfo;
    private readonly IDownloadScheduler _scheduler;
    private readonly IDownloadRepository _repository;
    private readonly IDownloadFolder _downloadFolder;
    private readonly ISaveLocationPicker _picker;
    private readonly DialogSaveLocationPicker _dialogs;
    private readonly IOptionsMonitor<DownloadOptions> _options;
    private readonly ILogger<MainWindowViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _address = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private DownloadItemViewModel? _selected;

    [ObservableProperty]
    private FilterOptionViewModel? _selectedFilter;

    [ObservableProperty]
    private CategoryOptionViewModel? _selectedCategory;

    [ObservableProperty]
    private bool _isDetailOpen = true;

    [ObservableProperty]
    private string _totalsText = "Nothing downloading";

    [ObservableProperty]
    private string _totalSpeedText = string.Empty;

    public MainWindowViewModel(
        IApplicationInfoService applicationInfo,
        IDownloadScheduler scheduler,
        IDownloadRepository repository,
        IDownloadFolder downloadFolder,
        ISaveLocationPicker picker,
        DialogSaveLocationPicker dialogs,
        IOptionsMonitor<DownloadOptions> options,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _scheduler = scheduler;
        _repository = repository;
        _downloadFolder = downloadFolder;
        _picker = picker;
        _dialogs = dialogs;
        _options = options;
        _logger = logger;

        Filters = [.. Enum.GetValues<TransferFilter>().Select(value => new FilterOptionViewModel(value))];
        _selectedFilter = Filters[0];

        Categories = [.. SidebarCategories.Select(category => new CategoryOptionViewModel(category))];

        All.CollectionChanged += OnAllChanged;
    }

    /// <summary>Every transfer. The table shows a filtered view of this.</summary>
    public ObservableCollection<DownloadItemViewModel> All { get; } = [];

    public ObservableCollection<DownloadItemViewModel> Visible { get; } = [];

    public ObservableCollection<FilterOptionViewModel> Filters { get; }

    public ObservableCollection<CategoryOptionViewModel> Categories { get; }

    public string Name => _applicationInfo.Name;

    public string FullName => _applicationInfo.FullName;

    public string Version => $"v{_applicationInfo.Version}";

    public string DownloadFolderText => _downloadFolder.GetPath();

    public string ConnectionBudgetText => $"{_options.CurrentValue.MaximumConnectionsPerHost} conn / host";

    public bool HasDownloads => All.Count > 0;

    public bool HasActiveDownloads => All.Any(download => download.IsActive);

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
                Track(DownloadItemViewModel.Restore(_scheduler, _repository, _logger, job));
            }

            _logger.LogInformation("Restored {Count} transfers from the previous session.", All.Count);
        }
        catch (Exception exception)
        {
            // A broken database must not stop the application from starting.
            _logger.LogError(exception, "Could not restore the previous session's transfers.");
            ErrorMessage = "Previous downloads could not be restored.";
        }

        Refresh();
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

        if (_options.CurrentValue.AskWhereToSave)
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

        Track(item, atTop: true);
        Selected = item;

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
        DownloadProbe probe;

        try
        {
            probe = await _scheduler.ProbeAsync(address);
        }
        catch (Exception exception) when (exception is DownloadFailedException or ArgumentException)
        {
            // Worth showing the dialog anyway: the transfer will ask the server again,
            // and a name from the URL is better than refusing to start.
            _logger.LogWarning(exception, "Could not look up {Address} before saving.", address);

            string guessed = SafeFileName.FromUri(source);
            probe = new DownloadProbe(
                guessed, null, null, FileCategories.Resolve(guessed), SupportsResume: false);
        }

        return await _picker.PickAsync(address, probe, _downloadFolder.GetPath());
    }

    [RelayCommand]
    private Task OpenSettingsAsync() => _dialogs.ShowSettingsAsync();

    /// <summary>
    /// Status and category are two ways of narrowing the same list, so choosing one
    /// clears the other rather than quietly combining into an empty result.
    /// </summary>
    partial void OnSelectedFilterChanged(FilterOptionViewModel? value)
    {
        if (value is not null)
        {
            SelectedCategory = null;
            Refresh();
        }
    }

    partial void OnSelectedCategoryChanged(CategoryOptionViewModel? value)
    {
        if (value is not null)
        {
            SelectedFilter = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void ToggleDetail() => IsDetailOpen = !IsDetailOpen;

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        foreach (DownloadItemViewModel finished in All.Where(item => !item.IsActive).ToList())
        {
            // Removing a paused or failed row is the user abandoning it, so its partial
            // file goes too rather than lingering in the download folder for ever.
            if (!finished.IsCompleted)
            {
                finished.DiscardPartial();
            }

            Untrack(finished);
            await finished.ForgetAsync();
            finished.Dispose();
        }

        Refresh();
    }

    /// <summary>
    /// Stops every running transfer and waits for it to unwind before the process exits.
    /// Called while the window is closing.
    /// </summary>
    public async Task ShutdownAsync()
    {
        // Closing is not abandoning: partial files are kept so the transfers can be
        // resumed the next time the application starts.
        await Task.WhenAll(All.Select(download => download.StopAndWaitAsync(keepPartialFile: true)));

        foreach (DownloadItemViewModel download in All)
        {
            download.Dispose();
        }
    }

    private void Track(DownloadItemViewModel item, bool atTop = false)
    {
        // A row's status changes which filter it belongs to and every count beside it, so
        // the list listens to each row rather than re-reading them on a timer.
        item.PropertyChanged += OnItemChanged;

        if (atTop)
        {
            All.Insert(0, item);
        }
        else
        {
            All.Add(item);
        }
    }

    private void Untrack(DownloadItemViewModel item)
    {
        item.PropertyChanged -= OnItemChanged;
        All.Remove(item);

        if (ReferenceEquals(Selected, item))
        {
            Selected = null;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadItemViewModel.Status)
            or nameof(DownloadItemViewModel.CategoryName))
        {
            Refresh();
        }
        else if (e.PropertyName is nameof(DownloadItemViewModel.SpeedText))
        {
            UpdateTotals();
        }
    }

    private void OnAllChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasDownloads));
        OnPropertyChanged(nameof(HasActiveDownloads));
        Refresh();
    }

    /// <summary>Rebuilds the visible rows and every count in the sidebar.</summary>
    private void Refresh()
    {
        DownloadItemViewModel? keep = Selected;

        Visible.Clear();

        foreach (DownloadItemViewModel item in All.Where(Matches))
        {
            Visible.Add(item);
        }

        foreach (FilterOptionViewModel option in Filters)
        {
            option.Count = All.Count(item => TransferFilters.Matches(option.Filter, item));
        }

        foreach (CategoryOptionViewModel category in Categories)
        {
            category.Count = All.Count(
                item => string.Equals(item.CategoryName, category.Name, StringComparison.Ordinal));
        }

        // Clearing the collection drops the selection, so it is put back rather than
        // making the detail panel blink every time a row changes status.
        Selected = keep is not null && Visible.Contains(keep) ? keep : Visible.FirstOrDefault();

        UpdateTotals();
    }

    private bool Matches(DownloadItemViewModel item)
    {
        if (SelectedCategory is { } category)
        {
            return string.Equals(item.CategoryName, category.Name, StringComparison.Ordinal);
        }

        return TransferFilters.Matches(SelectedFilter?.Filter ?? TransferFilter.All, item);
    }

    private void UpdateTotals()
    {
        int running = All.Count(item => item.Status is DownloadStatus.Running);
        int queued = All.Count(item => item.Status is DownloadStatus.Pending);
        int paused = All.Count(item => item.Status is DownloadStatus.Paused);

        TotalsText = All.Count == 0
            ? "Nothing downloading"
            : $"{running} active · {queued} queued · {paused} paused";

        double bytesPerSecond = All.Sum(item => item.BytesPerSecond);
        TotalSpeedText = bytesPerSecond > 0 ? FormatBytes((long)bytesPerSecond) + "/s" : string.Empty;
    }

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
}

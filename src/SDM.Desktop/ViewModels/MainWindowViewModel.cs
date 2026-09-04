using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Application.Integration;
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
    private readonly ISystemShell _shell;
    private readonly IOptionsMonitor<DownloadOptions> _options;
    private readonly IBrowserBridge _bridge;
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
        ISystemShell shell,
        IOptionsMonitor<DownloadOptions> options,
        IBrowserBridge bridge,
        ILogger<MainWindowViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _scheduler = scheduler;
        _repository = repository;
        _downloadFolder = downloadFolder;
        _picker = picker;
        _dialogs = dialogs;
        _shell = shell;
        _options = options;
        _bridge = bridge;
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
                Track(DownloadItemViewModel.Restore(_scheduler, _repository, _shell, _logger, job));
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

        // Started only once the list is restored, so a link arriving in the first second
        // is not lost or duplicated against a half-built list.
        _bridge.DownloadRequested += OnBrowserRequest;
        _bridge.ShowRequested += OnShowRequest;

        try
        {
            await _bridge.StartAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The window is already open and the list is already restored. A bridge that
            // will not start costs the browser handover and nothing else, so it is
            // reported where the user can see it rather than taken as a startup failure.
            _logger.LogError(exception, "The browser bridge could not start.");
            ErrorMessage = "The browser bridge could not start. Links from the browser will not arrive.";
        }
    }

    /// <summary>
    /// A second launch of SDM asking this copy to come forward. Raised on the bridge's
    /// own thread, so the window is touched from the interface thread.
    /// </summary>
    private void OnShowRequest(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => ShowRequested?.Invoke(this, EventArgs.Empty));

    /// <summary>
    /// Asks the window to bring itself forward. The view model does not own the window
    /// and cannot raise it; the window listens for this and does.
    /// </summary>
    public event EventHandler? ShowRequested;

    /// <summary>
    /// A link handed over by the browser. The bridge raises this on its own thread, so
    /// the work is posted to the interface thread before touching the list.
    /// </summary>
    private void OnBrowserRequest(object? sender, BridgeMessage message)
    {
        if (message.Url is not { Length: > 0 } url)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (AlreadyInFlight(url) is { } existing)
            {
                _logger.LogInformation("The browser sent {Url}, which this list is already downloading.", url);
                Selected = existing;
                ErrorMessage = $"{existing.FileName} is already in the list.";
                return;
            }

            DownloadItemViewModel item = DownloadItemViewModel.Create(
                _scheduler, _repository, _shell, _logger, url,
                context: message.ToRequestContext(),
                suggestedFileName: message.FileName);

            Track(item, atTop: true);
            Selected = item;
            _ = item.RunAsync();
        });
    }

    /// <summary>
    /// The row already working on this address, if there is one.
    ///
    /// Two rows for one address is not a duplicate in the list: it is two transfers
    /// writing into the same partial file at once, because the file a transfer resumes
    /// from is found by its URL. They interleave their bytes and the result is a corrupt
    /// file that both rows report as finished. A double-click on a download button is
    /// enough to cause it.
    ///
    /// Finished and cancelled rows are not in the way — they own nothing on disk that a
    /// new transfer would collide with. A failed one does: its partial file is still
    /// there waiting for the resume button, so it counts.
    /// </summary>
    private DownloadItemViewModel? AlreadyInFlight(string address) =>
        All.FirstOrDefault(item =>
            !item.IsFinished
            && string.Equals(item.Address, address.Trim(), StringComparison.OrdinalIgnoreCase));

    public string BridgeAddress => _bridge.Address;

    public bool IsBridgeRunning => _bridge.IsRunning;

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

        if (AlreadyInFlight(address) is { } running)
        {
            Selected = running;
            ErrorMessage = $"{running.FileName} is already in the list.";
            return;
        }

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
            _scheduler, _repository, _shell, _logger, address, destination);

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

    private void OnActionFailed(object? sender, string message) => ErrorMessage = message;

    private void OnRemoveRequested(object? sender, TransferRemoval removal)
    {
        if (sender is DownloadItemViewModel item)
        {
            _ = RemoveAsync(item, removal);
        }
    }

    /// <summary>
    /// Removes one row. A running transfer is stopped first and waited for: taking the row
    /// away while its connections are still writing would leave the file growing with
    /// nothing on screen owning it, and deleting the file underneath them would fail.
    /// </summary>
    private async Task RemoveAsync(DownloadItemViewModel item, TransferRemoval removal)
    {
        ErrorMessage = null;

        // Asked before anything is stopped, because the answer decides whether to stop it
        // at all. One menu entry away from "Remove from list" sits a button that deletes a
        // finished download from disk, and a menu is opened by the same click that picks
        // an entry in it — nothing about that gesture should be able to destroy a file the
        // user spent an hour fetching.
        if (removal == TransferRemoval.DeleteFile && !await ConfirmDeletionAsync(item))
        {
            return;
        }

        await item.StopAndWaitAsync(keepPartialFile: removal == TransferRemoval.KeepFile);

        if (removal == TransferRemoval.DeleteFile)
        {
            item.DeleteFromDisk();
        }

        Untrack(item);
        await item.ForgetAsync();
        item.Dispose();

        Refresh();
    }

    /// <summary>
    /// Names the file and says plainly what will happen to it. A finished transfer owns a
    /// real file; an unfinished one owns only the partial file it would have resumed from,
    /// and losing that means starting the download again — different losses, so they are
    /// described differently rather than behind one word.
    /// </summary>
    private Task<bool> ConfirmDeletionAsync(DownloadItemViewModel item) =>
        item.IsCompleted
            ? _dialogs.ConfirmAsync(
                "Delete this file?",
                $"{item.FileName} will be removed from the list and deleted from disk. This cannot be undone.",
                "Delete file")
            : _dialogs.ConfirmAsync(
                "Discard this download?",
                $"{item.FileName} is not finished. Removing it deletes what has been downloaded so far, "
                + "so it would have to start again from the beginning.",
                "Discard download");

    /// <summary>
    /// Clears the rows the user is done with. This used to take everything that was not
    /// running — which included paused and failed transfers — and delete their partial
    /// files with it, so a transfer stopped at 90% was destroyed, without confirmation,
    /// by a button promising to clear what had finished.
    ///
    /// Throwing a partial transfer away is a deliberate, per-row act: it is the cancel
    /// button on the row itself. Nothing here touches the disk.
    /// </summary>
    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        foreach (DownloadItemViewModel finished in All.Where(item => item.IsFinished).ToList())
        {
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

        // Rows that were already finished still hold unwritten state if their last save
        // was still in flight, so every row is flushed, not only the running ones.
        await Task.WhenAll(All.Select(download => download.FlushAsync()));

        foreach (DownloadItemViewModel download in All)
        {
            download.Dispose();
        }

        _bridge.DownloadRequested -= OnBrowserRequest;
        _bridge.ShowRequested -= OnShowRequest;
        await _bridge.DisposeAsync();
    }

    private void Track(DownloadItemViewModel item, bool atTop = false)
    {
        // A row's status changes which filter it belongs to and every count beside it, so
        // the list listens to each row rather than re-reading them on a timer.
        item.PropertyChanged += OnItemChanged;
        item.RemoveRequested += OnRemoveRequested;
        item.ActionFailed += OnActionFailed;

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
        item.RemoveRequested -= OnRemoveRequested;
        item.ActionFailed -= OnActionFailed;
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

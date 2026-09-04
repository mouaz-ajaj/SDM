using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SDM.Application.Downloads;
using SDM.Core.Downloads;
using SDM.Desktop.Services;

namespace SDM.Desktop.ViewModels;

/// <summary>
/// One row in the transfer list. Owns its own cancellation source, so pausing or
/// cancelling a row never touches its neighbours, and writes itself to the repository
/// on every state change so the list survives the application closing.
/// </summary>
public sealed partial class DownloadItemViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    // Speed measured over a single 100 ms progress tick swings wildly. Blending each
    // sample into a running average gives a figure that is readable rather than jittery.
    private const double SmoothingFactor = 0.3;
    private const double MinimumSampleSeconds = 0.15;

    /// <summary>
    /// How long a cancelled transfer is given to unwind before the window stops waiting.
    /// Long enough for a connection to notice, short enough not to hang an exit.
    /// </summary>
    private static readonly TimeSpan UnwindTimeout = TimeSpan.FromSeconds(10);

    private readonly IDownloadScheduler _scheduler;
    private readonly IDownloadRepository _repository;
    private readonly ISystemShell _shell;
    private readonly ILogger _logger;
    private readonly Guid _id;
    private readonly string _address;
    private readonly DownloadDestination? _destination;

    /// <summary>
    /// The browser session this transfer came from, when it came from one. Held in memory
    /// only and never written to the database: a cookie is a credential, and a list of
    /// downloads is not the place to keep live sessions on disk.
    /// </summary>
    private readonly RequestContext? _context;
    private readonly DateTimeOffset _createdAt;

    private CancellationTokenSource _cancellation = new();

    /// <summary>The transfer in flight, so stopping the row can wait for it by name.</summary>
    private Task _running = Task.CompletedTask;

    private long _bytesReceived;
    private long? _totalBytes;
    private string? _mediaType;
    private FileCategory _category = FileCategory.Other;
    private long _lastBytes;
    private long _lastTimestamp;
    private double _bytesPerSecond;

    /// <summary>Exposed so the status bar can add every row up.</summary>
    public double BytesPerSecond => _bytesPerSecond;
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
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsResumable))]
    [NotifyPropertyChangedFor(nameof(ShowsProgress))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(PercentageText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    private DownloadStatus _status = DownloadStatus.Pending;

    [ObservableProperty]
    private string _detail = "Queued";

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <summary>Where the engine decided to write. Known once the response headers arrive.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestination))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveWithFileCommand))]
    private string? _destinationPath;

    [ObservableProperty]
    private bool _serverSupportsResume = true;

    /// <summary>Shown when the transfer is split, so the speed figure is explicable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _connectionsText = string.Empty;

    /// <summary>
    /// Every byte has arrived and the file is being checked and moved into place. Still
    /// Running as far as the list is concerned, but no longer downloading.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isVerifying;

    /// <summary>
    /// Waiting out the backoff before another attempt. The row stays Pending, because it
    /// genuinely is waiting for its turn to run — but "queued behind other transfers" and
    /// "this one just failed and is about to try again" are not the same thing, and the
    /// status column said "Queued" for both while the reason sat out of sight in the
    /// detail panel.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _retryText = string.Empty;

    /// <summary>Documents, Video, Programs — what the file was sorted as.</summary>
    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private IBrush _categoryBrush = new SolidColorBrush(Color.Parse("#64798F"));

    [ObservableProperty]
    private string _mediaTypeText = "Not reported";

    [ObservableProperty]
    private string _sizeText = string.Empty;

    [ObservableProperty]
    private string _resumeText = string.Empty;

    /// <summary>One row per connection, updated in place so the bars do not flicker.</summary>
    public ObservableCollection<SegmentViewModel> Segments { get; } = [];

    /// <summary>What happened to this transfer, newest last.</summary>
    public ObservableCollection<DownloadEventViewModel> History { get; } = [];

    private DownloadItemViewModel(
        IDownloadScheduler scheduler,
        IDownloadRepository repository,
        ISystemShell shell,
        ILogger logger,
        Guid id,
        string address,
        DateTimeOffset createdAt,
        DownloadDestination? destination = null,
        RequestContext? context = null)
    {
        _destination = destination;
        _context = context;
        _scheduler = scheduler;
        _repository = repository;
        _shell = shell;
        _logger = logger;
        _id = id;
        _address = address;
        _createdAt = createdAt;
        _fileName = PreviewFileName(address);
    }

    public static DownloadItemViewModel Create(
        IDownloadScheduler scheduler,
        IDownloadRepository repository,
        ISystemShell shell,
        ILogger logger,
        string address,
        DownloadDestination? destination = null,
        RequestContext? context = null,
        string? suggestedFileName = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        DownloadItemViewModel item = new(
            scheduler, repository, shell, logger,
            Guid.NewGuid(), address.Trim(), DateTimeOffset.UtcNow, destination, context);

        // Only what the row shows until the server answers. It is not passed to the
        // engine, which settles the real name from Content-Disposition — but a name the
        // browser already knew beats one guessed from a URL that ends in an opaque id,
        // and it is what the user was looking at when they clicked.
        if (!string.IsNullOrWhiteSpace(suggestedFileName))
        {
            item.FileName = SafeFileName.Sanitize(suggestedFileName);
        }

        item.Persist();
        return item;
    }

    /// <summary>Rebuilds a row from the previous run.</summary>
    public static DownloadItemViewModel Restore(
        IDownloadScheduler scheduler,
        IDownloadRepository repository,
        ISystemShell shell,
        ILogger logger,
        DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(shell);

        DownloadItemViewModel item = new(
            scheduler, repository, shell, logger, job.Id, job.Address, job.CreatedAt,
            DestinationFor(job))
        {
            DestinationPath = job.DestinationPath,
            _bytesReceived = job.BytesReceived,
            _totalBytes = job.TotalBytes,
            _mediaType = job.MediaType,
            _category = job.Category,
            CategoryName = FileCategories.FolderNameFor(job.Category),
            CategoryBrush = new SolidColorBrush(Color.Parse(CategoryColours.HexFor(job.Category))),
            IsIndeterminate = false,
        };

        if (job.DestinationPath is { Length: > 0 } path)
        {
            item.FileName = Path.GetFileName(path);
        }

        // Nothing is transferring in a process that has only just started, so anything
        // recorded as running or queued is really paused, waiting for the resume button.
        item.Status = job.Status is DownloadStatus.Running or DownloadStatus.Pending
            ? DownloadStatus.Paused
            : job.Status;

        item.Percentage = job.TotalBytes is > 0
            ? (double)job.BytesReceived / job.TotalBytes.Value * 100d
            : 0;

        item.Detail = item.Status switch
        {
            DownloadStatus.Completed => job.Detail ?? "Complete",
            DownloadStatus.Paused when job.TotalBytes is { } total =>
                $"Paused at {FormatBytes(job.BytesReceived)} of {FormatBytes(total)}",
            DownloadStatus.Paused => "Paused",
            _ => job.Detail ?? item.Status.ToString(),
        };

        return item;
    }

    /// <summary>
    /// Gives a restored row back the destination the user picked for it, and only then.
    ///
    /// Resuming looks for the partial file inside the folder the transfer is told to
    /// write into. A row restored without its folder was told the default one, found
    /// nothing there, and started the whole download again — into the default folder,
    /// not the drive the user had chosen. Both halves of that were wrong.
    ///
    /// A row SDM sorted itself is deliberately left alone: handing back the category
    /// folder as though the user had chosen it would turn off the sorting and the
    /// "name (1)" that keeps a second copy from overwriting the first.
    /// </summary>
    private static DownloadDestination? DestinationFor(DownloadJob job) =>
        job is { ChosenByUser: true, DestinationPath: { Length: > 0 } path }
        && Path.GetDirectoryName(path) is { Length: > 0 } directory
            ? new DownloadDestination(directory, Path.GetFileName(path))
            : null;

    public string Address => _address;

    public bool IsActive => Status is DownloadStatus.Pending or DownloadStatus.Running;

    public bool IsCompleted => Status is DownloadStatus.Completed;

    public bool IsPaused => Status is DownloadStatus.Paused;

    /// <summary>Paused and failed rows can both be picked up again from their partial file.</summary>
    public bool IsResumable => Status is DownloadStatus.Paused or DownloadStatus.Failed;

    /// <summary>
    /// The transfer is over and nothing on disk is waiting on it: it either completed, or
    /// the user threw it away with the row's own cancel button. Deliberately not the
    /// inverse of <see cref="IsActive"/> — a paused or failed row is stopped but not
    /// finished, and it still owns a partial file and a resume button.
    /// </summary>
    public bool IsFinished => Status is DownloadStatus.Completed or DownloadStatus.Cancelled;

    /// <summary>The engine has settled on a path, so there is a folder worth opening.</summary>
    public bool HasDestination => DestinationPath is { Length: > 0 };

    public bool ShowsProgress => IsActive || IsPaused;

    /// <summary>
    /// Rounded down, not to nearest, while the transfer is still running: 99.6% rounds to
    /// "100%", and a row reading 100% with a live speed beside it looks stuck rather than
    /// nearly done. Only a finished transfer is allowed to say 100.
    /// </summary>
    public string PercentageText => IsCompleted
        ? "100%"
        : Math.Floor(Percentage).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>One word for the table's status column, where the long detail will not fit.</summary>
    public string StatusText => Status switch
    {
        DownloadStatus.Pending when RetryText.Length > 0 => RetryText,
        DownloadStatus.Pending => "Queued",
        DownloadStatus.Running when IsVerifying => "Verifying",
        DownloadStatus.Running => string.IsNullOrEmpty(ConnectionsText) ? "Downloading" : ConnectionsText,
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };

    /// <summary>
    /// Runs the transfer to completion. Never throws: failures become status. Called on
    /// the interface thread, which is where everything it writes has to be written.
    /// </summary>
    /// <remarks>
    /// The task is kept so that stopping the row can wait for the real thing rather than
    /// for a guess about how long unwinding takes.
    /// </remarks>
    public Task RunAsync() => _running = RunCoreAsync();

    private async Task RunCoreAsync()
    {
        _pauseRequested = false;

        // Progress<T> posts to the interface thread by itself. The four below are plain
        // delegates the engine invokes wherever it happens to be standing, so each is
        // marshalled by hand: every one writes observable properties, and two of them add
        // to collections the window is bound to — which a thread pool thread may not do.
        DownloadCallbacks callbacks = new()
        {
            Progress = new Progress<DownloadProgress>(OnProgress),
            Segments = new Progress<IReadOnlyList<SegmentProgress>>(OnSegments),
            Planned = plan => OnInterfaceThread(() => OnPlanned(plan)),
            Retrying = retry => OnInterfaceThread(() => OnRetry(retry)),
            Started = () => OnInterfaceThread(OnStarted),
            Verifying = () => OnInterfaceThread(OnVerifying),
        };

        try
        {
            // Task.Run, so the transfer begins on a thread pool thread with no
            // synchronization context to capture. Awaited directly, the whole engine ran
            // on the interface thread instead — every await inside it resumed there, so
            // each 80 KB read, each write, the folder scan that looks for a partial file
            // and the final move of a finished file were all pumped through the very
            // dispatcher the window draws on.
            DownloadResult result = await Task.Run(
                () => _scheduler.EnqueueAsync(
                    _address, callbacks, _destination, _context, _cancellation.Token),
                CancellationToken.None);

            DestinationPath = result.DestinationPath;
            FileName = Path.GetFileName(result.DestinationPath);
            _bytesReceived = result.BytesWritten;
            _totalBytes = result.BytesWritten;
            _mediaType = result.MediaType ?? _mediaType;
            _category = result.Category;
            CategoryName = FileCategories.FolderNameFor(result.Category);
            CategoryBrush = new SolidColorBrush(Color.Parse(CategoryColours.HexFor(result.Category)));
            Percentage = 100;
            IsIndeterminate = false;

            foreach (SegmentViewModel segment in Segments)
            {
                segment.MarkComplete();
            }

            SpeedText = string.Empty;
            RemainingText = string.Empty;
            Settle(
                DownloadStatus.Completed,
                $"{FormatBytes(result.BytesWritten)} · {Path.GetDirectoryName(result.DestinationPath)}");
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
        catch (Exception exception)
        {
            // The clauses above name the failures that were expected, and the list was
            // not complete: HttpRequestException raised while a split transfer opens its
            // parts reaches here, and so does anything a future change adds. Because this
            // method is started and not awaited, every one of those became an unobserved
            // task exception — the row sat on "Downloading" for ever, with no message, no
            // failure, and a resume button that could not be pressed because the row was
            // still, as far as it knew, running.
            //
            // A row that cannot say what went wrong is worse than one that says something
            // vague, so nothing gets out of here uncaught.
            _logger.LogError(exception, "Transfer {JobId} failed unexpectedly.", _id);
            Settle(DownloadStatus.Failed, $"Transfer failed: {exception.Message}");
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
        await _cancellation.CancelAsync();

        // The transfer's own task, waited for directly. This used to poll IsActive every
        // 20 ms and give up after two seconds — a guess at how long unwinding takes, which
        // is wrong in both directions: it waits when the transfer stopped at once, and it
        // walks away while the transfer is still writing when a connection is slow to
        // notice the cancellation. Walking away is what mattered: the row's final state
        // was then written while it was still changing.
        //
        // The wait is still bounded, because the window is closing and nothing here is
        // worth hanging an exit on. RunAsync turns every failure into status and never
        // throws, so there is nothing to catch from the task itself.
        try
        {
            await _running.WaitAsync(UnwindTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Transfer {JobId} did not stop within {Timeout}; its state is being written anyway.",
                _id,
                UnwindTimeout);
        }

        // Persist() runs detached everywhere else so it never blocks a transfer, but the
        // process is about to exit: this last write has to be waited for or the row's
        // final state is lost in the race.
        await FlushAsync();
    }

    /// <summary>Writes the row's current state and waits for it.</summary>
    public Task FlushAsync() => SaveAsync(Snapshot());

    /// <summary>Removes the partial file for a row the user is throwing away.</summary>
    public void DiscardPartial()
    {
        if (DestinationPath is { Length: > 0 } path)
        {
            _scheduler.Discard(path);
        }
    }

    /// <summary>Removes the row from the saved list.</summary>
    public async Task ForgetAsync()
    {
        try
        {
            await _repository.DeleteAsync(_id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not remove transfer {JobId} from the database.", _id);
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
        RetryText = string.Empty;
        IsIndeterminate = true;

        await RunAsync();
    }

    /// <summary>
    /// Asks the list to take this row away. A row cannot remove itself: it does not own
    /// the collection it is in, and the list has to stop the transfer and forget the row
    /// in the database as well.
    /// </summary>
    public event EventHandler<TransferRemoval>? RemoveRequested;

    /// <summary>
    /// Something the user asked for did not happen — the file has been moved, the shell
    /// refused. Raised rather than written into <see cref="Detail"/>, which is the
    /// transfer's own story and not the place for a failed double-click.
    /// </summary>
    public event EventHandler<string>? ActionFailed;

    [RelayCommand(CanExecute = nameof(IsCompleted))]
    private void OpenFile()
    {
        if (!_shell.Open(DestinationPath ?? string.Empty))
        {
            ActionFailed?.Invoke(this, $"{FileName} is no longer where SDM put it.");
        }
    }

    [RelayCommand(CanExecute = nameof(HasDestination))]
    private void OpenFolder()
    {
        if (!_shell.Reveal(DestinationPath ?? string.Empty))
        {
            ActionFailed?.Invoke(this, "That folder could not be opened.");
        }
    }

    [RelayCommand]
    private Task CopyLinkAsync() => _shell.CopyAsync(_address);

    [RelayCommand]
    private void RemoveFromList() => RemoveRequested?.Invoke(this, TransferRemoval.KeepFile);

    [RelayCommand(CanExecute = nameof(HasDestination))]
    private void RemoveWithFile() => RemoveRequested?.Invoke(this, TransferRemoval.DeleteFile);

    /// <summary>
    /// Removes what this row put on disk. A finished transfer owns the file itself; an
    /// unfinished one owns only a partial file, which the engine names and removes.
    /// </summary>
    public void DeleteFromDisk()
    {
        if (DestinationPath is not { Length: > 0 } path)
        {
            return;
        }

        if (!IsCompleted)
        {
            DiscardPartial();
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Most likely the file is open in another program. The row still goes.
            _logger.LogWarning(exception, "Could not delete {Path}.", path);
            ActionFailed?.Invoke(this, $"{FileName} is in use and was not deleted.");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the interface thread — immediately when already
    /// there, so a callback raised from the queue is not reordered behind work the
    /// dispatcher has yet to reach.
    /// </summary>
    private static void OnInterfaceThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void OnPlanned(DownloadPlan plan)
    {
        // The server answered, so whatever the last attempt failed on is over.
        RetryText = string.Empty;

        DestinationPath = plan.DestinationPath;
        FileName = Path.GetFileName(plan.DestinationPath);
        ServerSupportsResume = plan.ServerSupportsResume;
        _mediaType = plan.MediaType;
        _category = plan.Category;
        CategoryName = FileCategories.FolderNameFor(plan.Category);
        CategoryBrush = new SolidColorBrush(Color.Parse(CategoryColours.HexFor(plan.Category)));
        MediaTypeText = string.IsNullOrWhiteSpace(plan.MediaType) ? "Not reported" : plan.MediaType;
        SizeText = plan.TotalBytes is { } size ? FormatBytes(size) : "Unknown";
        ResumeText = plan.ServerSupportsResume ? "Yes — server accepts ranges" : "No — cannot be resumed";

        Record(
            DownloadEventKind.Information,
            plan.SegmentCount > 1
                ? $"Started across {plan.SegmentCount} connections · {SizeText}"
                : $"Started on one connection · {SizeText}");

        _totalBytes = plan.TotalBytes;
        _bytesReceived = plan.ResumedFrom;
        PauseCommand.NotifyCanExecuteChanged();

        if (plan.ResumedFrom > 0)
        {
            Detail = $"Resuming from {FormatBytes(plan.ResumedFrom)}";
        }

        // Anchor the rate window at the resume point so the first sample is not a spike.
        _lastBytes = plan.ResumedFrom;
        _lastTimestamp = Stopwatch.GetTimestamp();

        // The destination is worth recording immediately: without it a killed process
        // leaves a partial file that no row knows how to claim.
        Persist();
    }

    private void OnSegments(IReadOnlyList<SegmentProgress> segments)
    {
        // Updated in place rather than rebuilt: replacing the collection every 100 ms
        // would make the bars flicker and lose the scroll position.
        for (int index = 0; index < segments.Count; index++)
        {
            if (index < Segments.Count)
            {
                Segments[index].Update(segments[index]);
            }
            else
            {
                Segments.Add(new SegmentViewModel(segments[index]));
            }
        }

        while (Segments.Count > segments.Count)
        {
            Segments.RemoveAt(Segments.Count - 1);
        }
    }

    private void Record(DownloadEventKind kind, string text) =>
        History.Add(new DownloadEventViewModel(new DownloadEvent(DateTimeOffset.Now, kind, text)));

    private void OnRetry(DownloadRetry retry)
    {
        Status = DownloadStatus.Pending;
        RetryText = $"Retry {retry.Attempt}/{retry.MaximumAttempts}";
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

    /// <summary>
    /// The bytes are all in and the file is being checked and moved into place. The speed
    /// and the estimate go now rather than at the end: both stopped meaning anything the
    /// moment the last byte arrived, and leaving them on screen is what made a transfer
    /// waiting on the disk look like one that had stalled at 100%.
    /// </summary>
    private void OnVerifying()
    {
        IsVerifying = true;
        Detail = "Verifying…";
        SpeedText = string.Empty;
        RemainingText = string.Empty;
        _bytesPerSecond = 0;
    }

    /// <summary>
    /// A row that has settled is final, and a late report must not raise it from the
    /// dead. The engine reports progress one last time from inside Complete, after the
    /// file has been moved — and <see cref="Progress{T}"/> posts that to the interface
    /// thread, so it can arrive *after* the awaited call has already returned and the row
    /// has been marked complete. It then set the status back to Running, which is why a
    /// finished download sat at 100% still labelled "Downloading" while its own history
    /// said "Finished": both were true, in that order.
    /// </summary>
    private bool HasSettled => Status
        is DownloadStatus.Completed
        or DownloadStatus.Failed
        or DownloadStatus.Cancelled
        or DownloadStatus.Paused;

    private void OnProgress(DownloadProgress progress)
    {
        if (HasSettled)
        {
            return;
        }

        Status = DownloadStatus.Running;
        IsIndeterminate = progress.TotalBytes is null;
        Percentage = progress.Percentage ?? 0;
        _bytesReceived = progress.BytesReceived;
        _totalBytes = progress.TotalBytes;

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

    /// <summary>
    /// Records a final state. Every call is a meaningful transition, which is exactly
    /// when the row is written to the database — never on a timer, and never per chunk.
    /// </summary>
    private void Settle(DownloadStatus status, string detail)
    {
        Status = status;
        Detail = detail;
        IsVerifying = false;
        RetryText = string.Empty;
        IsIndeterminate = false;
        SpeedText = string.Empty;
        RemainingText = string.Empty;
        _bytesPerSecond = 0;

        Record(
            status switch
            {
                DownloadStatus.Completed => DownloadEventKind.Success,
                DownloadStatus.Failed => DownloadEventKind.Warning,
                _ => DownloadEventKind.Information,
            },
            status switch
            {
                DownloadStatus.Completed => "Finished",
                DownloadStatus.Paused => "Paused — partial file kept",
                DownloadStatus.Cancelled => "Cancelled — partial file deleted",
                _ => detail,
            });

        Persist();
    }

    private void Persist()
    {
        // Saving must never take the window down or block the transfer, so it runs
        // detached and reports failures to the log instead.
        _ = SaveAsync(Snapshot());
    }

    private DownloadJob Snapshot()
    {
        return new DownloadJob
        {
            Id = _id,
            Address = _address,
            DestinationPath = DestinationPath,
            BytesReceived = _bytesReceived,
            TotalBytes = _totalBytes,
            Status = Status,
            Detail = Detail,
            MediaType = _mediaType,
            Category = _category,
            ChosenByUser = _destination is not null,
            CreatedAt = _createdAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private async Task SaveAsync(DownloadJob job)
    {
        try
        {
            await _repository.SaveAsync(job);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not save transfer {JobId}.", job.Id);
        }
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

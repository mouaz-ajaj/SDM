using Microsoft.Extensions.Logging.Abstractions;
using SDM.Application.Downloads;
using SDM.Core.Downloads;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task AddAsync_RefusesAnAddressThatIsAlreadyBeingDownloaded()
    {
        Harness harness = new();
        harness.Scheduler.OnEnqueue = (_, token) => FakeScheduler.BlockUntilCancelledAsync(token);

        harness.ViewModel.Address = "https://example.test/file.bin";
        await harness.ViewModel.AddCommand.ExecuteAsync(null);

        harness.ViewModel.Address = "https://example.test/file.bin";
        await harness.ViewModel.AddCommand.ExecuteAsync(null);

        // Two rows for one address is not a duplicate in a list: the partial file a
        // transfer resumes from is found by its URL, so both rows write into it at once
        // and both report the corrupt result as finished.
        Assert.Single(harness.ViewModel.All);
        Assert.Contains("already in the list", harness.ViewModel.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAsync_AllowsAnAddressWhoseEarlierDownloadHasFinished()
    {
        Harness harness = new();

        harness.ViewModel.Address = "https://example.test/file.bin";
        await harness.ViewModel.AddCommand.ExecuteAsync(null);
        await harness.ViewModel.All[0].RunAsync();

        harness.ViewModel.Address = "https://example.test/file.bin";
        await harness.ViewModel.AddCommand.ExecuteAsync(null);

        // A finished row owns nothing on disk that a new transfer would collide with, so
        // downloading the same file again is an ordinary thing to want.
        Assert.Equal(2, harness.ViewModel.All.Count);
    }

    [Fact]
    public async Task AddAsync_RejectsAnAddressThatIsNotHttp()
    {
        Harness harness = new();

        harness.ViewModel.Address = "ftp://example.test/file.bin";
        await harness.ViewModel.AddCommand.ExecuteAsync(null);

        Assert.Empty(harness.ViewModel.All);
        Assert.Contains("http", harness.ViewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingWithTheFile_AsksFirstAndDeletesNothingWhenRefused()
    {
        Harness harness = new();
        harness.Dialogs.Answer = false;

        harness.Repository.Restored = [FinishedJob()];
        await harness.ViewModel.LoadAsync();

        DownloadItemViewModel row = Assert.Single(harness.ViewModel.All);
        row.RemoveWithFileCommand.Execute(null);
        await WaitForAsync(() => harness.Dialogs.TimesAsked > 0);

        // One menu entry below "Remove from list" sits a button that deletes a finished
        // download from disk, reachable by the same click that opens the menu.
        Assert.Equal(1, harness.Dialogs.TimesAsked);
        Assert.Contains("cannot be undone", harness.Dialogs.LastMessage!, StringComparison.Ordinal);
        Assert.Single(harness.ViewModel.All);
        Assert.Empty(harness.Repository.Deleted);
    }

    [Fact]
    public async Task RemovingWithTheFile_ProceedsOnceConfirmed()
    {
        Harness harness = new();
        harness.Dialogs.Answer = true;

        harness.Repository.Restored = [FinishedJob()];
        await harness.ViewModel.LoadAsync();

        DownloadItemViewModel row = Assert.Single(harness.ViewModel.All);
        row.RemoveWithFileCommand.Execute(null);
        await WaitForAsync(() => harness.ViewModel.All.Count == 0);

        Assert.Equal(1, harness.Dialogs.TimesAsked);
        Assert.Single(harness.Repository.Deleted);
    }

    [Fact]
    public async Task RemovingFromTheListAlone_NeverAsks()
    {
        Harness harness = new();

        harness.Repository.Restored = [FinishedJob()];
        await harness.ViewModel.LoadAsync();

        DownloadItemViewModel row = Assert.Single(harness.ViewModel.All);
        row.RemoveFromListCommand.Execute(null);
        await WaitForAsync(() => harness.ViewModel.All.Count == 0);

        // Nothing on disk is touched, so there is nothing to confirm. A dialog for this
        // would teach people to dismiss the one that matters without reading it.
        Assert.Equal(0, harness.Dialogs.TimesAsked);
    }

    [Fact]
    public async Task Refresh_LeavesTheVisibleRowsAloneWhenNothingAboutThemChanged()
    {
        Harness harness = new();

        harness.Repository.Restored = [FinishedJob(), FinishedJob()];
        await harness.ViewModel.LoadAsync();

        DownloadItemViewModel[] before = [.. harness.ViewModel.Visible];
        int changes = 0;
        harness.ViewModel.Visible.CollectionChanged += (_, _) => changes++;

        // A category rename is exactly the kind of change that used to clear and refill
        // the whole collection: the rows shown are the same rows in the same order.
        harness.ViewModel.SelectedFilter = harness.ViewModel.Filters[0];

        Assert.Equal(0, changes);
        Assert.Equal(before, harness.ViewModel.Visible);
    }

    [Fact]
    public async Task Refresh_ShowsOnlyTheRowsMatchingTheChosenFilter()
    {
        Harness harness = new();

        harness.Repository.Restored = [FinishedJob(), PausedJob()];
        await harness.ViewModel.LoadAsync();

        harness.ViewModel.SelectedFilter =
            harness.ViewModel.Filters.Single(filter => filter.Filter == TransferFilter.Completed);

        DownloadItemViewModel shown = Assert.Single(harness.ViewModel.Visible);
        Assert.Equal(DownloadStatus.Completed, shown.Status);
    }

    [Fact]
    public async Task LoadAsync_SurvivesADatabaseThatCannotBeRead()
    {
        Harness harness = new();
        harness.Repository.OnGetAll = () => throw new InvalidOperationException("the file is corrupt");

        await harness.ViewModel.LoadAsync();

        // A broken database must not stop the application from starting.
        Assert.Empty(harness.ViewModel.All);
        Assert.NotNull(harness.ViewModel.ErrorMessage);
    }

    /// <summary>
    /// Removal runs detached from the command that asked for it, so the assertion has to
    /// wait for it rather than assume it has already happened.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "The expected change never happened.");
    }

    private static DownloadJob FinishedJob() => new()
    {
        Id = Guid.NewGuid(),
        Address = "https://example.test/done.bin",
        DestinationPath = @"C:\Downloads\done.bin",
        Status = DownloadStatus.Completed,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static DownloadJob PausedJob() => new()
    {
        Id = Guid.NewGuid(),
        Address = "https://example.test/half.bin",
        DestinationPath = @"C:\Downloads\half.bin",
        Status = DownloadStatus.Paused,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class Harness
    {
        public Harness()
        {
            ViewModel = new MainWindowViewModel(
                new FakeApplicationInfo(),
                Scheduler,
                Repository,
                new FakeDownloadFolder(),
                new FakeSaveLocationPicker(),
                Dialogs,
                new FakeShell(),
                new StaticOptions<DownloadOptions>(new DownloadOptions()),
                Bridge,
                NullLogger<MainWindowViewModel>.Instance);
        }

        public FakeScheduler Scheduler { get; } = new();

        public FakeRepository Repository { get; } = new();

        public FakeDialogs Dialogs { get; } = new();

        public FakeBridge Bridge { get; } = new();

        public MainWindowViewModel ViewModel { get; }
    }
}

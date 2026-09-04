using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SDM.Core.Downloads;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Tests;

public sealed class DownloadItemViewModelTests
{
    [Fact]
    public async Task RunAsync_TurnsAnUnexpectedFailureIntoAFailedRow()
    {
        FakeScheduler scheduler = new()
        {
            // Not one of the exceptions the row used to name. This is raised for real when
            // a split transfer opens its parts, and it escaped every catch clause — which
            // made it an unobserved task exception, because the row's task is started and
            // not awaited. The row then sat on "Downloading" for ever: no message, no
            // failure, and a resume button it would not enable because it still believed
            // it was running.
            OnEnqueue = (_, _) => throw new HttpRequestException("the connection was refused"),
        };

        DownloadItemViewModel item = Create(scheduler);

        await item.RunAsync();

        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.True(item.IsResumable, "A failed row keeps its partial file and its resume button.");
        Assert.Contains("connection was refused", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RecordsACompletedTransfer()
    {
        FakeScheduler scheduler = new()
        {
            OnEnqueue = (_, _) => Task.FromResult(new DownloadResult(@"C:\Downloads\Video\clip.mp4", 2048)),
        };

        DownloadItemViewModel item = Create(scheduler);

        await item.RunAsync();

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("clip.mp4", item.FileName);
        Assert.Equal(@"C:\Downloads\Video\clip.mp4", item.DestinationPath);
    }

    [Fact]
    public async Task StopAndWaitAsync_WaitsForTheTransferItselfRatherThanAFixedDelay()
    {
        TaskCompletionSource unwound = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeScheduler scheduler = new()
        {
            OnEnqueue = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // Slower to notice the cancellation than the two seconds the row used
                    // to allow itself. That is the case that mattered: the row walked away
                    // while the transfer was still running and wrote its final state while
                    // it was still changing.
                    await Task.Delay(300, CancellationToken.None);
                    unwound.SetResult();
                    throw;
                }

                throw new UnreachableException();
            },
        };

        DownloadItemViewModel item = Create(scheduler);
        _ = item.RunAsync();

        await scheduler.Started;
        await item.StopAndWaitAsync(keepPartialFile: true);

        Assert.True(unwound.Task.IsCompletedSuccessfully, "The row returned before its transfer had unwound.");
        Assert.Equal(DownloadStatus.Paused, item.Status);
    }

    [Fact]
    public void Restore_GivesBackAFolderTheUserChose()
    {
        DownloadItemViewModel item = DownloadItemViewModel.Restore(
            new FakeScheduler(), new FakeRepository(), new FakeShell(), NullLogger.Instance,
            NewJob() with
            {
                DestinationPath = @"D:\Work\report.pdf",
                ChosenByUser = true,
                Status = DownloadStatus.Paused,
            });

        // Resuming looks for the partial file in the folder the transfer is told to write
        // into. Restored without that folder, the row looked in the default one, found
        // nothing, and downloaded the whole file again into the wrong place.
        Assert.Equal("report.pdf", item.FileName);
        Assert.True(item.IsResumable);
        Assert.Equal(@"D:\Work\report.pdf", item.DestinationPath);
    }

    [Fact]
    public async Task Restore_DoesNotTreatSdmsOwnSortingAsAChoiceTheUserMade()
    {
        FakeScheduler scheduler = new();

        DownloadItemViewModel item = DownloadItemViewModel.Restore(
            scheduler, new FakeRepository(), new FakeShell(), NullLogger.Instance,
            NewJob() with
            {
                // Where SDM sorted it, not where anyone asked for it.
                DestinationPath = @"C:\Downloads\Documents\report.pdf",
                ChosenByUser = false,
                Status = DownloadStatus.Paused,
            });

        scheduler.OnEnqueue = (_, _) => Task.FromResult(new DownloadResult(@"C:\Downloads\Documents\report.pdf", 1));

        await item.ResumeCommand.ExecuteAsync(null);

        // Handing the category folder back as though it had been chosen would switch off
        // the sorting and the "name (1)" that keeps a second copy from overwriting the
        // first, on every attempt after the first one.
        Assert.Null(Assert.Single(scheduler.Destinations));
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public async Task RunAsync_RecordsWhetherTheUserChoseTheDestination()
    {
        FakeRepository repository = new();
        DownloadItemViewModel item = DownloadItemViewModel.Create(
            new FakeScheduler(), repository, new FakeShell(), NullLogger.Instance,
            "https://example.test/file.bin",
            new DownloadDestination(@"D:\Work", "file.bin"));

        await item.RunAsync();

        Assert.All(repository.Saved, job => Assert.True(job.ChosenByUser));
    }

    [Fact]
    public void Create_PrefersTheNameTheBrowserAlreadyKnew()
    {
        DownloadItemViewModel item = DownloadItemViewModel.Create(
            new FakeScheduler(), new FakeRepository(), new FakeShell(), NullLogger.Instance,
            "https://example.test/download?id=8f21c0",
            suggestedFileName: "Quarterly report.pdf");

        // The URL ends in an opaque id, which is exactly when guessing produces "download".
        Assert.Equal("Quarterly report.pdf", item.FileName);
    }

    [Fact]
    public void Create_SanitisesTheNameTheBrowserSent()
    {
        DownloadItemViewModel item = DownloadItemViewModel.Create(
            new FakeScheduler(), new FakeRepository(), new FakeShell(), NullLogger.Instance,
            "https://example.test/download",
            suggestedFileName: @"../../escaped.txt");

        Assert.Equal("escaped.txt", item.FileName);
    }

    private static DownloadItemViewModel Create(FakeScheduler scheduler) =>
        DownloadItemViewModel.Create(
            scheduler, new FakeRepository(), new FakeShell(), NullLogger.Instance,
            "https://example.test/file.bin");

    private static DownloadJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Address = "https://example.test/report.pdf",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}

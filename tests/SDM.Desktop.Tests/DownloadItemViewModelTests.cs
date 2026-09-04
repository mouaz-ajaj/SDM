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
            new FakeScheduler(), new FakeRepository(), new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
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
            scheduler, new FakeRepository(), new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
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
            new FakeScheduler(), repository, new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
            "https://example.test/file.bin",
            new DownloadDestination(@"D:\Work", "file.bin"));

        await item.RunAsync();

        Assert.All(repository.Saved, job => Assert.True(job.ChosenByUser));
    }

    [Fact]
    public void Create_PrefersTheNameTheBrowserAlreadyKnew()
    {
        DownloadItemViewModel item = DownloadItemViewModel.Create(
            new FakeScheduler(), new FakeRepository(), new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
            "https://example.test/download?id=8f21c0",
            suggestedFileName: "Quarterly report.pdf");

        // The URL ends in an opaque id, which is exactly when guessing produces "download".
        Assert.Equal("Quarterly report.pdf", item.FileName);
    }

    [Fact]
    public void Create_SanitisesTheNameTheBrowserSent()
    {
        DownloadItemViewModel item = DownloadItemViewModel.Create(
            new FakeScheduler(), new FakeRepository(), new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
            "https://example.test/download",
            suggestedFileName: @"../../escaped.txt");

        Assert.Equal("escaped.txt", item.FileName);
    }

    [Fact]
    public async Task OnPlanned_FillsInEverythingTheServerJustSaid()
    {
        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, _) =>
            {
                callbacks?.Started?.Invoke();
                callbacks?.Planned?.Invoke(
                    new DownloadPlan(@"C:\Downloads\Video\real-name.mp4", 4096, 0, true, 3)
                    {
                        MediaType = "video/mp4",
                        Category = FileCategory.Video,
                    });

                return Task.FromResult(
                    new DownloadResult(@"C:\Downloads\Video\real-name.mp4", 4096)
                    {
                        MediaType = "video/mp4",
                        Category = FileCategory.Video,
                    });
            },
        };

        DownloadItemViewModel item = Create(scheduler);

        await item.RunAsync();

        // None of this was covered before the marshaller was injected: the callbacks were
        // posted to a dispatcher no test pumps, so they never ran and every assertion
        // about them would have passed against a row that had not been told anything.
        Assert.Equal("video/mp4", item.MediaTypeText);
        Assert.Equal("4 KB", item.SizeText);
        Assert.Equal("Video", item.CategoryName);
        Assert.StartsWith("Yes", item.ResumeText, StringComparison.Ordinal);
        Assert.Equal(3, item.ConnectionCount);
        Assert.Contains(item.History, entry => entry.Text.Contains("3 connections", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusText_SaysHowManyConnectionsAreRunningWhileTheyAre()
    {
        TaskCompletionSource planned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, token) =>
            {
                callbacks?.Started?.Invoke();
                callbacks?.Planned?.Invoke(new DownloadPlan(@"C:\Downloads\big.iso", 1 << 30, 0, true, 4));
                planned.SetResult();
                return FakeScheduler.BlockUntilCancelledAsync(token);
            },
        };

        DownloadItemViewModel item = Create(scheduler);
        _ = item.RunAsync();
        await planned.Task;

        // The status column could only ever read "Downloading", because the property this
        // branch tests was a string nothing assigned. The detail panel's Connections field
        // was blank for the same reason.
        Assert.Equal("4 connections", item.StatusText);
        Assert.Equal("4 running at once", item.ConnectionsText);

        await item.StopAndWaitAsync(keepPartialFile: true);
    }

    [Fact]
    public async Task StatusText_DoesNotClutterTheColumnForAnUnsplitTransfer()
    {
        TaskCompletionSource planned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, token) =>
            {
                callbacks?.Started?.Invoke();
                callbacks?.Planned?.Invoke(new DownloadPlan(@"C:\Downloads\small.txt", 900, 0, false, 1));
                planned.SetResult();
                return FakeScheduler.BlockUntilCancelledAsync(token);
            },
        };

        DownloadItemViewModel item = Create(scheduler);
        _ = item.RunAsync();
        await planned.Task;

        Assert.Equal("Downloading", item.StatusText);
        Assert.Equal("1 — this file is not split", item.ConnectionsText);

        // A server that refuses ranges cannot be paused, only cancelled.
        Assert.False(item.ServerSupportsResume);
        Assert.False(item.PauseCommand.CanExecute(null));

        await item.StopAndWaitAsync(keepPartialFile: true);
    }

    [Fact]
    public async Task OnRetry_SaysWhichAttemptIsComingAndWhy()
    {
        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, _) =>
            {
                callbacks?.Started?.Invoke();
                callbacks?.Retrying?.Invoke(
                    new DownloadRetry(2, 4, TimeSpan.FromSeconds(3), "Server answered 503 ServiceUnavailable"));

                return Task.FromResult(new DownloadResult(@"C:\Downloads\file.bin", 10));
            },
        };

        DownloadItemViewModel item = Create(scheduler);
        TaskCompletionSource<string> whileRetrying = new(TaskCreationOptions.RunContinuationsAsynchronously);

        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadItemViewModel.RetryText) && item.RetryText.Length > 0)
            {
                whileRetrying.TrySetResult(item.StatusText);
            }
        };

        await item.RunAsync();

        // "Queued behind other transfers" and "this one just failed and is about to try
        // again" both used to read as "Queued", with the reason out of sight.
        Assert.Equal("Retry 2/4", await whileRetrying.Task);
        Assert.Equal(DownloadStatus.Completed, item.Status);
    }

    [Fact]
    public async Task OnVerifying_StopsClaimingASpeedOnceTheLastByteIsIn()
    {
        TaskCompletionSource verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, token) =>
            {
                callbacks?.Started?.Invoke();
                callbacks?.Planned?.Invoke(new DownloadPlan(@"C:\Downloads\big.iso", 4096, 0, true, 1));
                callbacks?.Verifying?.Invoke();
                verifying.SetResult();
                return FakeScheduler.BlockUntilCancelledAsync(token);
            },
        };

        DownloadItemViewModel item = Create(scheduler);
        _ = item.RunAsync();
        await verifying.Task;

        // A large file is not moved into place instantly, and leaving the speed and the
        // estimate on screen is what made a transfer waiting on the disk look stalled.
        Assert.Equal("Verifying", item.StatusText);
        Assert.Equal(string.Empty, item.SpeedText);
        Assert.Equal(string.Empty, item.RemainingText);

        await item.StopAndWaitAsync(keepPartialFile: true);
    }

    [Fact]
    public async Task History_DoesNotGrowForEverAcrossManyAttempts()
    {
        FakeScheduler scheduler = new()
        {
            OnEnqueue = (callbacks, _) =>
            {
                callbacks?.Planned?.Invoke(new DownloadPlan(@"C:\Downloads\file.bin", 10, 0, true, 1));
                return Task.FromResult(new DownloadResult(@"C:\Downloads\file.bin", 10));
            },
        };

        DownloadItemViewModel item = Create(scheduler);

        // A row paused and resumed all afternoon writes two lines per attempt, and nothing
        // ever removed one.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            await item.RunAsync();
        }

        Assert.True(
            item.History.Count <= 200,
            $"The history kept {item.History.Count} entries and is only ever appended to.");
    }

    private static DownloadItemViewModel Create(FakeScheduler scheduler) =>
        DownloadItemViewModel.Create(
            scheduler, new FakeRepository(), new FakeShell(), new ImmediateUiThread(), NullLogger.Instance,
            "https://example.test/file.bin");

    private static DownloadJob NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Address = "https://example.test/report.pdf",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}

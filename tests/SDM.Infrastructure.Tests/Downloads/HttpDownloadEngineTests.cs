using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Tests.Downloads;

public sealed class HttpDownloadEngineTests : IDisposable
{
    private const int PayloadSize = 5 * 1024 * 1024;

    private readonly byte[] _payload = CreateDeterministicPayload(PayloadSize);
    private readonly byte[] _small = CreateDeterministicPayload(4096);
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("sdm-tests-").FullName;

    [Fact]
    public async Task DownloadAsync_WritesTheServedBytesToDisk()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("payload.bin"), _workingDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_workingDirectory, "payload.bin"), result.DestinationPath);
        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Equal(Hash(_payload), Hash(written));
        Assert.Single(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgressRepeatedlyAndEndsAtTheFullLength()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        List<DownloadProgress> reports = [];

        await engine.DownloadAsync(
            new DownloadRequest(server.Url("slow.bin"), _workingDirectory),
            Watching(reports),
            TestContext.Current.CancellationToken);

        Assert.True(reports.Count >= 2, "Expected repeated progress reports, got " + reports.Count);
        Assert.Equal(PayloadSize, reports[^1].BytesReceived);
        Assert.Equal(PayloadSize, reports[^1].TotalBytes);
        Assert.Equal(100d, reports[^1].Percentage);

        long previous = 0;
        foreach (DownloadProgress report in reports)
        {
            Assert.True(report.BytesReceived >= previous, "Progress went backwards.");
            previous = report.BytesReceived;
        }
    }

    [Fact]
    public async Task DownloadAsync_WhenCancelled_KeepsThePartialFileSoItCanBeResumed()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        await CancelPartWayThroughAsync(engine, server.Url("resumable.bin"));

        string partial = Path.Combine(_workingDirectory, "resumable.bin.part");

        Assert.True(File.Exists(partial), "Cancelling must leave something to resume from.");
        Assert.True(File.Exists(partial + ".meta"), "The partial file must record which URL it belongs to.");
        Assert.False(File.Exists(Path.Combine(_workingDirectory, "resumable.bin")));
    }

    [Fact]
    public async Task DownloadAsync_ResumesFromThePartialFileAndProducesTheCorrectBytes()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        long interrupted = await CancelPartWayThroughAsync(engine, server.Url("resumable.bin"));

        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("resumable.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.NotNull(plan);
        Assert.Equal(interrupted, plan!.ResumedFrom);
        Assert.True(plan.ResumedFrom > 0, "The second attempt should have continued, not restarted.");
        Assert.Equal(PayloadSize, plan.TotalBytes);
        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Equal(Hash(_payload), Hash(written));
        Assert.Single(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_WhenTheServerIgnoresRange_StartsAgainRatherThanCorruptTheFile()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        await CancelPartWayThroughAsync(engine, server.Url("ignores-range.bin"));

        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("ignores-range.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(0, plan!.ResumedFrom);
        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Equal(Hash(_payload), Hash(written));
    }

    [Fact]
    public async Task DownloadAsync_NeverResumesAPartialFileThatBelongsToADifferentUrl()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        // Leaves resumable.bin.part behind, owned by /resumable.bin.
        await CancelPartWayThroughAsync(engine, server.Url("resumable.bin"));

        // A different URL whose last segment is also "resumable.bin". Appending this
        // server's bytes to the other one's partial would silently corrupt the file.
        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("decoy/resumable.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(0, plan!.ResumedFrom);
        Assert.Equal(Path.Combine(_workingDirectory, "resumable (1).bin"), result.DestinationPath);
        Assert.Equal(Hash(_small), Hash(written));
        Assert.True(File.Exists(Path.Combine(_workingDirectory, "resumable.bin.part")),
            "The unrelated partial file must be left alone.");
    }

    [Fact]
    public async Task DownloadAsync_SplitsALargeRangeCapableFileAcrossSeveralConnections()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(maximumSegments: 4, segmentThresholdBytes: 1024);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("resumable.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(4, plan!.SegmentCount);
        Assert.Equal(PayloadSize, result.BytesWritten);

        // The whole point: four connections writing into one file must reassemble it
        // byte for byte, in the right order, with no gap or overlap.
        Assert.Equal(Hash(_payload), Hash(written));
        Assert.Single(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotSplitAFileBelowTheThreshold()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(
            maximumSegments: 4, segmentThresholdBytes: PayloadSize * 2L);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadPlan? plan = null;
        await engine.DownloadAsync(
            new DownloadRequest(server.Url("resumable.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, plan!.SegmentCount);
    }

    [Fact]
    public async Task DownloadAsync_DoesNotSplitAServerThatRefusesRanges()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(maximumSegments: 4, segmentThresholdBytes: 1024);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("ignores-range.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan!.SegmentCount);
        Assert.False(plan.ServerSupportsResume);
        Assert.Equal(Hash(_payload), Hash(written));
    }

    [Fact]
    public async Task DownloadAsync_ResumesASplitTransferFromItsRecordedSegmentPositions()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(maximumSegments: 4, segmentThresholdBytes: 1024);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        await CancelPartWayThroughAsync(engine, server.Url("resumable.bin"));

        // A split file is pre-allocated at full size, so its own length says nothing about
        // progress: the sidecar's per-segment positions are the only record.
        string sidecar = await File.ReadAllTextAsync(
            Path.Combine(_workingDirectory, "resumable.bin.part.meta"), TestContext.Current.CancellationToken);
        Assert.Contains("\"segments\"", sidecar, StringComparison.Ordinal);

        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("resumable.bin"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(4, plan!.SegmentCount);
        Assert.True(plan.ResumedFrom > 0, "The resumed transfer should have kept its earlier bytes.");
        Assert.Equal(Hash(_payload), Hash(written));
        Assert.Single(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_SortsTheFinishedFileIntoItsCategoryFolder()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(organizeIntoCategoryFolders: true);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("opaque-id"), _workingDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        // Content-Disposition names it a .pdf, so it belongs under Documents.
        Assert.Equal(
            Path.Combine(_workingDirectory, "Documents", "quarterly report.pdf"),
            result.DestinationPath);
        Assert.Equal(FileCategory.Documents, result.Category);
        Assert.True(File.Exists(result.DestinationPath));
    }

    [Fact]
    public async Task DownloadAsync_ReportsTheServersTypeInThePlan()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadPlan? plan = null;
        await engine.DownloadAsync(
            new DownloadRequest(server.Url("typed-video"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        Assert.Equal("video/mp4", plan!.MediaType);
        Assert.Equal(FileCategory.Video, plan.Category);
    }

    [Fact]
    public async Task DownloadAsync_ResumesAPartialThatLivesInACategoryFolder()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(organizeIntoCategoryFolders: true);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        await CancelPartWayThroughAsync(engine, server.Url("resumable.zip"));

        string partial = Path.Combine(_workingDirectory, "Compressed", "resumable.zip.part");
        Assert.True(File.Exists(partial), "The partial file should be inside the category folder.");

        // Looking only at the top level would miss it and silently start over.
        DownloadPlan? plan = null;
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("resumable.zip"), _workingDirectory),
            new DownloadCallbacks { Planned = value => plan = value },
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.True(plan!.ResumedFrom > 0, "The transfer should have continued, not restarted.");
        Assert.Equal(Path.Combine(_workingDirectory, "Compressed", "resumable.zip"), result.DestinationPath);
        Assert.Equal(Hash(_payload), Hash(written));
    }

    [Fact]
    public async Task ProbeAsync_LearnsTheRealNameSizeAndTypeWithoutDownloading()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        // The URL says "opaque-id"; only the server knows it is a PDF called something else.
        DownloadProbe probe = await engine.ProbeAsync(
            server.Url("opaque-id"), TestContext.Current.CancellationToken);

        Assert.Equal("quarterly report.pdf", probe.FileName);
        Assert.Equal(FileCategory.Documents, probe.Category);
        Assert.Empty(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task ProbeAsync_ReportsTheSizeAndWhetherTheServerAcceptsRanges()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadProbe resumable = await engine.ProbeAsync(
            server.Url("resumable.bin"), TestContext.Current.CancellationToken);
        DownloadProbe plain = await engine.ProbeAsync(
            server.Url("ignores-range.bin"), TestContext.Current.CancellationToken);

        Assert.Equal(PayloadSize, resumable.TotalBytes);
        Assert.True(resumable.SupportsResume);
        Assert.False(plain.SupportsResume);
    }

    [Fact]
    public async Task ProbeAsync_SurfacesAFailureRatherThanGuessing()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadFailedException exception = await Assert.ThrowsAsync<DownloadFailedException>(
            () => engine.ProbeAsync(server.Url("missing.bin"), TestContext.Current.CancellationToken));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task DownloadAsync_WritesExactlyWhereTheUserChose()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(organizeIntoCategoryFolders: true);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        string chosen = Path.Combine(_workingDirectory, "picked");
        Directory.CreateDirectory(chosen);

        // Sorting is on, so without the explicit choice this would land in Documents.
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("opaque-id"), chosen, "my report.pdf", chosenByUser: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(chosen, "my report.pdf"), result.DestinationPath);
    }

    [Fact]
    public async Task DownloadAsync_ReplacesAFileTheUserChoseToReplace()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        string existing = Path.Combine(_workingDirectory, "report.pdf");
        await File.WriteAllTextAsync(existing, "old", TestContext.Current.CancellationToken);

        // The save dialog already asked about replacing, so answering it with
        // "report (1).pdf" would ignore what the user just said.
        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("opaque-id"), _workingDirectory, "report.pdf", chosenByUser: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(existing, result.DestinationPath);
        Assert.Equal(_small.Length, new FileInfo(existing).Length);
    }

    [Fact]
    public async Task DiscardPartial_RemovesThePartialFileAndItsMetadata()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        await CancelPartWayThroughAsync(engine, server.Url("resumable.bin"));

        engine.DiscardPartial(Path.Combine(_workingDirectory, "resumable.bin"));

        Assert.Empty(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_WhenServerReturnsNotFound_ThrowsAndCreatesNoFile()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadFailedException exception = await Assert.ThrowsAsync<DownloadFailedException>(
            () => engine.DownloadAsync(
                new DownloadRequest(server.Url("missing.bin"), _workingDirectory),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(404, exception.StatusCode);
        Assert.False(exception.IsTransient, "A 404 is permanent and must not be retried.");
        Assert.Empty(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_MarksRateLimitingAsTransientAndCarriesRetryAfter()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadFailedException exception = await Assert.ThrowsAsync<DownloadFailedException>(
            () => engine.DownloadAsync(
                new DownloadRequest(server.Url("rate-limited"), _workingDirectory),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(429, exception.StatusCode);
        Assert.True(exception.IsTransient, "429 means come back later, not give up.");
        Assert.Equal(TimeSpan.FromSeconds(5), exception.RetryAfter);
        Assert.Empty(Directory.GetFiles(_workingDirectory));
    }

    [Fact]
    public async Task DownloadAsync_FailsWhenTheServerGoesSilentMidTransfer()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider(idleTimeoutSeconds: 1);
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadFailedException exception = await Assert.ThrowsAsync<DownloadFailedException>(
            () => engine.DownloadAsync(
                new DownloadRequest(server.Url("stalls"), _workingDirectory),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(exception.IsTransient);
        Assert.Contains("stopped sending data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_WhenLengthIsUnknown_StillWritesTheWholeBody()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        List<DownloadProgress> reports = [];

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("chunked.bin"), _workingDirectory),
            Watching(reports),
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(result.DestinationPath, TestContext.Current.CancellationToken);

        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Null(reports[0].TotalBytes);
        Assert.Null(reports[0].Percentage);
        Assert.Equal(Hash(_payload), Hash(written));
    }

    [Fact]
    public async Task DownloadAsync_TakesTheFileNameFromContentDisposition()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("opaque-id"), _workingDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_workingDirectory, "quarterly report.pdf"), result.DestinationPath);
    }

    [Fact]
    public async Task DownloadAsync_CannotBeTrickedIntoWritingOutsideTheDestinationDirectory()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("hostile"), _workingDirectory),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_workingDirectory, "escaped.txt"), result.DestinationPath);
        Assert.Equal(_workingDirectory, Path.GetDirectoryName(result.DestinationPath));
        Assert.False(File.Exists(Path.Combine(_workingDirectory, "..", "..", "escaped.txt")));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotOverwriteAnExistingFile()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        DownloadRequest request = new(server.Url("opaque-id"), _workingDirectory);

        DownloadResult first = await engine.DownloadAsync(
            request, cancellationToken: TestContext.Current.CancellationToken);
        DownloadResult second = await engine.DownloadAsync(
            request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_workingDirectory, "quarterly report.pdf"), first.DestinationPath);
        Assert.Equal(Path.Combine(_workingDirectory, "quarterly report (1).pdf"), second.DestinationPath);
        Assert.True(File.Exists(first.DestinationPath));
    }

    [Fact]
    public async Task DownloadAsync_CreatesTheDestinationDirectory()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        string nested = Path.Combine(_workingDirectory, "nested", "deeper");

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("payload.bin"), nested),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.DestinationPath));
        Assert.Equal(nested, Path.GetDirectoryName(result.DestinationPath));
    }

    /// <summary>Starts a transfer and cancels it once some bytes have landed.</summary>
    private async Task<long> CancelPartWayThroughAsync(IDownloadEngine engine, Uri source)
    {
        using CancellationTokenSource cancellation = new();
        long received = 0;

        DownloadCallbacks callbacks = new()
        {
            Progress = new SynchronousProgress<DownloadProgress>(report =>
            {
                received = report.BytesReceived;
                cancellation.Cancel();
            }),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.DownloadAsync(new DownloadRequest(source, _workingDirectory), callbacks, cancellation.Token));

        return received;
    }

    private static DownloadCallbacks Watching(List<DownloadProgress> reports) =>
        new() { Progress = new SynchronousProgress<DownloadProgress>(reports.Add) };

    private static ServiceProvider BuildProvider(
        int idleTimeoutSeconds = 60,
        int maximumSegments = 1,
        long segmentThresholdBytes = long.MaxValue,
        bool organizeIntoCategoryFolders = false)
    {
        DownloadOptions options = new()
        {
            IdleTimeoutSeconds = idleTimeoutSeconds,
            MaximumSegments = maximumSegments,
            MaximumConnectionsPerHost = maximumSegments + 1,
            SegmentThresholdBytes = segmentThresholdBytes,
            OrganizeIntoCategoryFolders = organizeIntoCategoryFolders,
        };

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IOptions<DownloadOptions>>(Options.Create(options));
        services.AddSingleton<IOptionsMonitor<DownloadOptions>>(new TestOptions<DownloadOptions>(options));
        services.AddSingleton<IConnectionBudget>(new HostConnectionBudget(Options.Create(options)));
        services.AddSingleton<IDownloadLayout>(
            new CategoryDownloadLayout(new TestOptions<DownloadOptions>(options)));
        services.AddSdmInfrastructure();
        return services.BuildServiceProvider();
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        switch (context.Request.Url?.AbsolutePath)
        {
            case "/payload.bin":
                context.Response.ContentLength64 = _payload.Length;
                await context.Response.OutputStream.WriteAsync(_payload, cancellationToken);
                break;

            case "/slow.bin":
                context.Response.ContentLength64 = _payload.Length;
                await WriteInChunksAsync(context, 0, cancellationToken);
                break;

            case "/chunked.bin":
                context.Response.SendChunked = true;
                await WriteInChunksAsync(context, 0, cancellationToken);
                break;

            case "/resumable.bin":
            case "/resumable.zip":
                await ServeRangeAsync(context, cancellationToken);
                break;

            case "/typed-video":
                context.Response.ContentType = "video/mp4";
                context.Response.ContentLength64 = _small.Length;
                await context.Response.OutputStream.WriteAsync(_small, cancellationToken);
                break;

            case "/ignores-range.bin":
                // Answers 200 with the whole body even when a Range was asked for, which
                // is what a server without range support does.
                context.Response.ContentLength64 = _payload.Length;
                await WriteInChunksAsync(context, 0, cancellationToken);
                break;

            case "/decoy/resumable.bin":
                context.Response.ContentLength64 = _small.Length;
                await context.Response.OutputStream.WriteAsync(_small, cancellationToken);
                break;

            case "/opaque-id":
                context.Response.AddHeader("Content-Disposition", "attachment; filename=\"quarterly report.pdf\"");
                context.Response.ContentLength64 = _small.Length;
                await context.Response.OutputStream.WriteAsync(_small, cancellationToken);
                break;

            case "/hostile":
                context.Response.AddHeader("Content-Disposition", "attachment; filename=\"../../escaped.txt\"");
                context.Response.ContentLength64 = _small.Length;
                await context.Response.OutputStream.WriteAsync(_small, cancellationToken);
                break;

            case "/rate-limited":
                context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                context.Response.AddHeader("Retry-After", "5");
                break;

            case "/stalls":
                // Headers and a first chunk arrive, then the connection is held open
                // saying nothing — the case an infinite HttpClient timeout cannot catch.
                context.Response.ContentLength64 = _payload.Length;
                await context.Response.OutputStream.WriteAsync(_small, cancellationToken);
                await context.Response.OutputStream.FlushAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                break;

            default:
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                break;
        }
    }

    /// <summary>A server that honours <c>Range</c> the way a real download host does.</summary>
    private async Task ServeRangeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        context.Response.AddHeader("Accept-Ranges", "bytes");
        context.Response.AddHeader("ETag", "\"payload-v1\"");

        (bool ranged, long start, long end) = ParseRange(context.Request.Headers["Range"], _payload.Length);

        if (ranged)
        {
            // RFC 7233: a server that honours a range answers 206 with Content-Range —
            // including for an open-ended "bytes=0-", which is how a client discovers
            // that the resource can be split at all.
            context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
            context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{_payload.Length}");
        }

        context.Response.ContentLength64 = end - start + 1;
        await WriteInChunksAsync(context, start, end, cancellationToken);
    }

    private static (bool Ranged, long Start, long End) ParseRange(string? header, long length)
    {
        const string Prefix = "bytes=";

        if (header is null || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, length - 1);
        }

        string[] parts = header[Prefix.Length..].Split('-');
        long start = long.TryParse(parts[0], out long parsedStart) ? parsedStart : 0;
        long end = parts.Length > 1 && long.TryParse(parts[1], out long parsedEnd) ? parsedEnd : length - 1;

        return (true, start, Math.Min(end, length - 1));
    }

    private Task WriteInChunksAsync(HttpListenerContext context, long from, CancellationToken cancellationToken) =>
        WriteInChunksAsync(context, from, _payload.Length - 1, cancellationToken);

    private async Task WriteInChunksAsync(
        HttpListenerContext context, long from, long to, CancellationToken cancellationToken)
    {
        const int ChunkSize = 64 * 1024;

        for (long offset = from; offset <= to; offset += ChunkSize)
        {
            int length = (int)Math.Min(ChunkSize, to - offset + 1);

            await context.Response.OutputStream.WriteAsync(
                _payload.AsMemory((int)offset, length), cancellationToken);
            await context.Response.OutputStream.FlushAsync(cancellationToken);

            // Slow enough that the transfer spans several progress intervals and can be
            // cancelled mid-flight, fast enough to keep the suite quick.
            await Task.Delay(15, cancellationToken);
        }
    }

    private static byte[] CreateDeterministicPayload(int size)
    {
        byte[] payload = new byte[size];
        new Random(20260831).NextBytes(payload);
        return payload;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail an otherwise passing test.
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        // Progress<T> hops through the synchronization context, which would let the
        // transfer finish before the cancellation test ever observes a report.
        public void Report(T value) => onReport(value);
    }
}

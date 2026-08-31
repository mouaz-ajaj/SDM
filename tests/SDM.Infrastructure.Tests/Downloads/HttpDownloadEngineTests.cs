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
        Assert.False(File.Exists(result.DestinationPath + ".part"));
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
            new SynchronousProgress<DownloadProgress>(reports.Add),
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
    public async Task DownloadAsync_WhenCancelledMidTransfer_LeavesNothingBehind()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();

        using CancellationTokenSource cancellation = new();
        SynchronousProgress<DownloadProgress> progress = new(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(
            new DownloadRequest(server.Url("slow.bin"), _workingDirectory),
            progress,
            cancellation.Token));

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
        Assert.Empty(Directory.GetFiles(_workingDirectory));
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
            new SynchronousProgress<DownloadProgress>(reports.Add),
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

    private static ServiceProvider BuildProvider(int idleTimeoutSeconds = 60)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IOptions<DownloadOptions>>(
            Options.Create(new DownloadOptions { IdleTimeoutSeconds = idleTimeoutSeconds }));
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
                await WriteInChunksAsync(context, cancellationToken);
                break;

            case "/chunked.bin":
                context.Response.SendChunked = true;
                await WriteInChunksAsync(context, cancellationToken);
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

    private async Task WriteInChunksAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        const int ChunkSize = 64 * 1024;

        for (int offset = 0; offset < _payload.Length; offset += ChunkSize)
        {
            int length = Math.Min(ChunkSize, _payload.Length - offset);

            await context.Response.OutputStream.WriteAsync(_payload.AsMemory(offset, length), cancellationToken);
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

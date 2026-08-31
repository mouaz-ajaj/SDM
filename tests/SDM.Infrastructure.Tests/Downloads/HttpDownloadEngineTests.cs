using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Tests.Downloads;

public sealed class HttpDownloadEngineTests : IDisposable
{
    private const int PayloadSize = 5 * 1024 * 1024;

    private readonly byte[] _payload = CreateDeterministicPayload(PayloadSize);
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("sdm-tests-").FullName;

    [Fact]
    public async Task DownloadAsync_WritesTheServedBytesToDisk()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        string destination = Path.Combine(_workingDirectory, "payload.bin");

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("file"), destination),
            cancellationToken: TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(destination, result.DestinationPath);
        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Equal(Hash(_payload), Hash(written));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgressRepeatedlyAndEndsAtTheFullLength()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        List<DownloadProgress> reports = [];

        await engine.DownloadAsync(
            new DownloadRequest(server.Url("slow"), Path.Combine(_workingDirectory, "progress.bin")),
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
        string destination = Path.Combine(_workingDirectory, "cancelled.bin");

        using CancellationTokenSource cancellation = new();
        SynchronousProgress<DownloadProgress> progress = new(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(
            new DownloadRequest(server.Url("slow"), destination),
            progress,
            cancellation.Token));

        Assert.False(File.Exists(destination), "A cancelled download must not leave a file at the destination.");
        Assert.False(File.Exists(destination + ".part"), "The partial file must be cleaned up.");
    }

    [Fact]
    public async Task DownloadAsync_WhenServerReturnsNotFound_ThrowsAndCreatesNoFile()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        string destination = Path.Combine(_workingDirectory, "missing.bin");

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => engine.DownloadAsync(
                new DownloadRequest(server.Url("missing"), destination),
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_WhenLengthIsUnknown_StillWritesTheWholeBody()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        List<DownloadProgress> reports = [];
        string destination = Path.Combine(_workingDirectory, "chunked.bin");

        DownloadResult result = await engine.DownloadAsync(
            new DownloadRequest(server.Url("chunked"), destination),
            new SynchronousProgress<DownloadProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        byte[] written = await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(PayloadSize, result.BytesWritten);
        Assert.Null(reports[0].TotalBytes);
        Assert.Null(reports[0].Percentage);
        Assert.Equal(Hash(_payload), Hash(written));
    }

    [Fact]
    public async Task DownloadAsync_CreatesTheDestinationDirectory()
    {
        using LocalHttpServer server = new(ServeAsync);
        await using ServiceProvider provider = BuildProvider();
        IDownloadEngine engine = provider.GetRequiredService<IDownloadEngine>();
        string destination = Path.Combine(_workingDirectory, "nested", "deeper", "payload.bin");

        await engine.DownloadAsync(
            new DownloadRequest(server.Url("file"), destination),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(destination));
    }

    private static ServiceProvider BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSdmInfrastructure();
        return services.BuildServiceProvider();
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        switch (context.Request.Url?.AbsolutePath)
        {
            case "/file":
                context.Response.ContentLength64 = _payload.Length;
                await context.Response.OutputStream.WriteAsync(_payload, cancellationToken);
                break;

            case "/slow":
                context.Response.ContentLength64 = _payload.Length;
                await WriteInChunksAsync(context, cancellationToken);
                break;

            case "/chunked":
                context.Response.SendChunked = true;
                await WriteInChunksAsync(context, cancellationToken);
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

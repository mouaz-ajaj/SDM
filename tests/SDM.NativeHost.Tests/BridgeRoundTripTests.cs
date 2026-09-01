using Microsoft.Extensions.Logging.Abstractions;
using SDM.Application.ApplicationInfo;
using SDM.Application.Integration;
using SDM.Infrastructure.Integration;
using SDM.NativeHost;

namespace SDM.NativeHost.Tests;

/// <summary>
/// The host and the application talking to each other over the real named pipe. Mocking
/// the pipe would test the parts that were never in doubt and skip the one that was.
/// </summary>
public sealed class BridgeRoundTripTests : IAsyncLifetime
{
    // A private pipe. The real name belongs to whatever copy of SDM is running, and a
    // test that quietly talks to the live application instead of its own bridge proves
    // nothing — which is exactly what happened the first time these were written.
    private readonly string _pipeName = $"sdm.test.{Guid.NewGuid():N}";

    private readonly NamedPipeBrowserBridge _bridge;

    public BridgeRoundTripTests() =>
        _bridge = new NamedPipeBrowserBridge(
            new StubApplicationInfo(), NullLogger<NamedPipeBrowserBridge>.Instance, _pipeName);

    private readonly List<BridgeMessage> _received = [];

    public async ValueTask InitializeAsync()
    {
        _bridge.DownloadRequested += (_, message) =>
        {
            lock (_received)
            {
                _received.Add(message);
            }
        };

        await _bridge.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ping_IsAnsweredWithTheApplicationsVersion()
    {
        BridgeReply reply = await Client().SendAsync(
            new BridgeMessage { Type = BridgeProtocol.Ping }, TestContext.Current.CancellationToken);

        Assert.Equal(BridgeProtocol.Pong, reply.Type);
        Assert.Equal("9.9.9", reply.Version);
    }

    [Fact]
    public async Task Download_IsAcceptedAndHandedToTheApplication()
    {
        BridgeReply reply = await Client().SendAsync(
            new BridgeMessage
            {
                Type = BridgeProtocol.Download,
                Url = "https://example.test/file.bin",
                FileName = "file.bin",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(BridgeProtocol.Accepted, reply.Type);

        BridgeMessage handed = Assert.Single(Snapshot());
        Assert.Equal("https://example.test/file.bin", handed.Url);
        Assert.Equal("file.bin", handed.FileName);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/config/SAM")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData(null)]
    public async Task Download_RefusesAnythingThatIsNotHttp(string? url)
    {
        // Everything arriving here was composed by an extension, so it is checked before
        // it reaches the download engine.
        BridgeReply reply = await Client().SendAsync(
            new BridgeMessage { Type = BridgeProtocol.Download, Url = url },
            TestContext.Current.CancellationToken);

        Assert.Equal(BridgeProtocol.Error, reply.Type);
        Assert.Empty(Snapshot());
    }

    [Fact]
    public async Task UnknownRequest_IsRefusedRatherThanIgnored()
    {
        // Silence would leave the extension waiting on the stream for ever.
        BridgeReply reply = await Client().SendAsync(
            new BridgeMessage { Type = "erase-everything" }, TestContext.Current.CancellationToken);

        Assert.Equal(BridgeProtocol.Error, reply.Type);
        Assert.Contains("erase-everything", reply.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bridge_KeepsServingAfterARefusedRequest()
    {
        await Client().SendAsync(
            new BridgeMessage { Type = BridgeProtocol.Download, Url = "nonsense" },
            TestContext.Current.CancellationToken);

        BridgeReply reply = await Client().SendAsync(
            new BridgeMessage { Type = BridgeProtocol.Ping }, TestContext.Current.CancellationToken);

        Assert.Equal(BridgeProtocol.Pong, reply.Type);
    }

    [Fact]
    public async Task Bridge_ServesSeveralBrowsersOneAfterAnother()
    {
        for (int index = 0; index < 4; index++)
        {
            BridgeReply reply = await Client().SendAsync(
                new BridgeMessage
                {
                    Type = BridgeProtocol.Download,
                    Url = $"https://example.test/file{index}.bin",
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(BridgeProtocol.Accepted, reply.Type);
        }

        Assert.Equal(4, Snapshot().Count);
    }

    [Fact]
    public void Address_NamesAPipeScopedToThisUser()
    {
        // A machine-wide pipe would let another signed-in user queue downloads here.
        Assert.Contains(
            Environment.UserName.ToLowerInvariant(),
            BridgeProtocol.PipeName,
            StringComparison.Ordinal);

        Assert.StartsWith(@"\\.\pipe\", _bridge.Address, StringComparison.Ordinal);
    }

    /// <summary>Never starts the application: a test must not spawn a window.</summary>
    private BridgeClient Client() =>
        new(connectTimeout: TimeSpan.FromSeconds(5), startApplication: () => false, pipeName: _pipeName);

    private List<BridgeMessage> Snapshot()
    {
        lock (_received)
        {
            return [.. _received];
        }
    }

    public async ValueTask DisposeAsync() => await _bridge.DisposeAsync();

    private sealed class StubApplicationInfo : IApplicationInfoService
    {
        public string Name => "SDM";

        public string FullName => "Speed Download Manager";

        public string Version => "9.9.9";
    }
}

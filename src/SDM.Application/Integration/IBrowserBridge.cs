namespace SDM.Application.Integration;

/// <summary>
/// Listens for downloads handed over by the browser. The bridge only carries requests —
/// it never transfers anything itself, so there is exactly one download engine no matter
/// how many browsers are connected.
/// </summary>
public interface IBrowserBridge : IAsyncDisposable
{
    /// <summary>Raised for every accepted request. Handlers run on a background thread.</summary>
    event EventHandler<BridgeMessage>? DownloadRequested;

    bool IsRunning { get; }

    /// <summary>The pipe the browser bridge connects to, for the settings screen.</summary>
    string Address { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
}

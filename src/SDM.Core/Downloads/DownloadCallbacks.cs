namespace SDM.Core.Downloads;

/// <summary>
/// Optional observation points for a transfer. Bundled into one object because passing
/// four independent delegates through three layers made every signature unreadable.
/// </summary>
public sealed record DownloadCallbacks
{
    /// <summary>Bytes transferred so far.</summary>
    public IProgress<DownloadProgress>? Progress { get; init; }

    /// <summary>Per-connection progress for a split transfer, throttled like Progress.</summary>
    public IProgress<IReadOnlyList<SegmentProgress>>? Segments { get; init; }

    /// <summary>Fires once the destination and size are known, before any byte is written.</summary>
    public Action<DownloadPlan>? Planned { get; init; }

    /// <summary>Fires before each retry, with the delay and the reason.</summary>
    public Action<DownloadRetry>? Retrying { get; init; }

    /// <summary>Fires when the transfer leaves the queue and actually begins.</summary>
    public Action? Started { get; init; }
}

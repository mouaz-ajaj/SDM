namespace SDM.Core.Downloads;

/// <summary>
/// A point-in-time snapshot of a transfer. <see cref="TotalBytes"/> is null when the
/// server does not advertise a length, in which case no percentage can be computed.
/// </summary>
public readonly record struct DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? (double)BytesReceived / TotalBytes.Value * 100d
        : null;
}

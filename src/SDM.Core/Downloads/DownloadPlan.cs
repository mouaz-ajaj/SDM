namespace SDM.Core.Downloads;

/// <summary>
/// What the engine settled on once the response headers arrived: where the file will be
/// written, what it is, how large, and how much of it was already on disk.
/// </summary>
public sealed record DownloadPlan(
    string DestinationPath,
    long? TotalBytes,
    long ResumedFrom,
    bool ServerSupportsResume,
    int SegmentCount)
{
    /// <summary>The server's Content-Type, when it sent one.</summary>
    public string? MediaType { get; init; }

    public FileCategory Category { get; init; } = FileCategory.Other;
}

namespace SDM.Core.Downloads;

/// <summary>
/// What the engine settled on once the response headers arrived: where the file will be
/// written, how large it is, and how much of it was already on disk.
/// </summary>
public sealed record DownloadPlan(
    string DestinationPath,
    long? TotalBytes,
    long ResumedFrom,
    bool ServerSupportsResume);

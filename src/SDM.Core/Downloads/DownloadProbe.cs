namespace SDM.Core.Downloads;

/// <summary>
/// What a URL turns out to be, learned without downloading it. Asking the server first
/// is what lets a save dialog show the real file name: a URL ending in an opaque id
/// tells you nothing, while its Content-Disposition names the file exactly.
/// </summary>
public sealed record DownloadProbe(
    string FileName,
    long? TotalBytes,
    string? MediaType,
    FileCategory Category,
    bool SupportsResume);

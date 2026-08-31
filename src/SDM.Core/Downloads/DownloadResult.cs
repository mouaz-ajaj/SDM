namespace SDM.Core.Downloads;

/// <summary>
/// The outcome of a completed transfer. <see cref="DestinationPath"/> is reported back
/// because later stages let the engine choose the final name from response headers.
/// </summary>
public sealed record DownloadResult(string DestinationPath, long BytesWritten);

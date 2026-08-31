namespace SDM.Core.Downloads;

/// <summary>
/// The outcome of a completed transfer. <see cref="DestinationPath"/> is reported back
/// because the engine chooses both the final name and, when sorting is on, the folder.
/// </summary>
public sealed record DownloadResult(string DestinationPath, long BytesWritten)
{
    public string? MediaType { get; init; }

    public FileCategory Category { get; init; } = FileCategory.Other;
}

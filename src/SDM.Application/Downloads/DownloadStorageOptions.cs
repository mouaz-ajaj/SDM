namespace SDM.Application.Downloads;

public sealed class DownloadStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Where the database lives. Empty means the per-user application data folder, which
    /// is the only sensible default: next to the executable is read-only once installed.
    /// </summary>
    public string DirectoryPath { get; init; } = string.Empty;

    public string FileName { get; init; } = "sdm.db";
}

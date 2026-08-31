using Microsoft.Extensions.Logging;

namespace SDM.Infrastructure.Logging;

public sealed class FileLogOptions
{
    public const string SectionName = "FileLog";

    /// <summary>Empty means the per-user application data folder.</summary>
    public string DirectoryPath { get; init; } = string.Empty;

    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

    /// <summary>Days of history to keep. Older files are removed at startup.</summary>
    public int RetainedDays { get; init; } = 7;

    /// <summary>A single file is rolled once it passes this size.</summary>
    public long MaximumFileBytes { get; init; } = 8 * 1024 * 1024;
}

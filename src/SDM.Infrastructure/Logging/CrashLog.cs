namespace SDM.Infrastructure.Logging;

/// <summary>
/// Records a failure that happened before, or instead of, normal logging. A desktop
/// application built as a Windows executable has no console, so a startup crash would
/// otherwise leave the user with a window that never appears and no explanation at all.
/// </summary>
public static class CrashLog
{
    public const string FileName = "startup-error.log";

    public static string Write(Exception exception, string? directoryPath = null)
    {
        string directory = SdmLogPaths.ResolveDirectory(directoryPath);
        string path = Path.Combine(directory, FileName);

        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                path,
                $"""

                ===== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} =====
                {exception}

                """);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // There is nowhere left to report to; losing the crash note must not itself
            // become the crash.
        }

        return path;
    }
}

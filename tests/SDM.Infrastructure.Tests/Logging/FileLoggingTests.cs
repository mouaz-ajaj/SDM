using System.Text;
using Microsoft.Extensions.Logging;
using SDM.Infrastructure.Logging;

namespace SDM.Infrastructure.Tests.Logging;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sdm-logs-").FullName;

    [Fact]
    public void Logger_WritesTheMessageToAFileOnDisk()
    {
        using FileLoggerProvider provider = new(Options());

        provider.CreateLogger("SDM.Test").LogInformation("Downloading {File}.", "payload.bin");

        Assert.Contains("Downloading payload.bin.", ReadAll(), StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_RecordsTheExceptionDetailNotJustTheMessage()
    {
        using FileLoggerProvider provider = new(Options());

        provider.CreateLogger("SDM.Test").LogError(
            new InvalidOperationException("the pipe was closed"), "Transfer failed.");

        string log = ReadAll();

        Assert.Contains("Transfer failed.", log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
        Assert.Contains("the pipe was closed", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_HonoursTheConfiguredMinimumLevel()
    {
        using FileLoggerProvider provider = new(Options(minimumLevel: LogLevel.Warning));
        ILogger logger = provider.CreateLogger("SDM.Test");

        logger.LogInformation("chatter");
        logger.LogWarning("something worth keeping");

        string log = ReadAll();

        Assert.DoesNotContain("chatter", log, StringComparison.Ordinal);
        Assert.Contains("something worth keeping", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_RollsToANewFileOnceTheSizeCapIsPassed()
    {
        using FileLoggerProvider provider = new(Options(maximumFileBytes: 512));
        ILogger logger = provider.CreateLogger("SDM.Test");

        for (int index = 0; index < 40; index++)
        {
            logger.LogInformation("Entry {Index} with enough text to fill the file quickly.", index);
        }

        Assert.True(
            Directory.GetFiles(_directory, "sdm-*.log").Length > 1,
            "A log that never rolls will eventually fill the user's disk.");
    }

    [Fact]
    public void Provider_RemovesLogsOlderThanTheRetentionWindow()
    {
        string stale = Path.Combine(_directory, "sdm-20200101.log");
        File.WriteAllText(stale, "ancient");
        File.SetLastWriteTime(stale, DateTime.Now.AddDays(-30));

        string fresh = Path.Combine(_directory, "sdm-20991231.log");
        File.WriteAllText(fresh, "recent");

        using FileLoggerProvider provider = new(Options(retainedDays: 7));

        Assert.False(File.Exists(stale), "Old logs must not accumulate for ever.");
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void CrashLog_RecordsAStartupFailureThatNoLoggerCouldHaveCaught()
    {
        // This is the case that used to vanish entirely: the container itself fails, so
        // there is no logger, and a Windows executable has no console to print to.
        InvalidOperationException failure = new("appsettings.json is missing");

        string path = CrashLog.Write(failure, _directory);
        string report = File.ReadAllText(path);

        Assert.Equal(Path.Combine(_directory, CrashLog.FileName), path);
        Assert.Contains("appsettings.json is missing", report, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashLog_AppendsRatherThanOverwritingAnEarlierReport()
    {
        CrashLog.Write(new InvalidOperationException("first failure"), _directory);
        CrashLog.Write(new InvalidOperationException("second failure"), _directory);

        string report = File.ReadAllText(Path.Combine(_directory, CrashLog.FileName));

        Assert.Contains("first failure", report, StringComparison.Ordinal);
        Assert.Contains("second failure", report, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDirectory_DefaultsToTheUsersApplicationDataFolder()
    {
        string resolved = SdmLogPaths.ResolveDirectory();

        Assert.Contains("SDM", resolved, StringComparison.Ordinal);
        Assert.EndsWith("logs", resolved, StringComparison.Ordinal);
    }

    private FileLogOptions Options(
        LogLevel minimumLevel = LogLevel.Information,
        int retainedDays = 7,
        long maximumFileBytes = 8 * 1024 * 1024) =>
        new()
        {
            DirectoryPath = _directory,
            MinimumLevel = minimumLevel,
            RetainedDays = retainedDays,
            MaximumFileBytes = maximumFileBytes,
        };

    /// <summary>
    /// Reads the log while the writer still holds it open. File.ReadAllText cannot: it
    /// asks for FileShare.Read, which forbids the writer's own write handle. Sharing has
    /// to be agreed by both sides, and a log nobody can read while the application runs
    /// would defeat the point of having one.
    /// </summary>
    private string ReadAll()
    {
        StringBuilder log = new();

        foreach (string path in Directory.GetFiles(_directory, "sdm-*.log"))
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            log.Append(reader.ReadToEnd());
        }

        return log.ToString();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail an otherwise passing test.
        }
    }
}

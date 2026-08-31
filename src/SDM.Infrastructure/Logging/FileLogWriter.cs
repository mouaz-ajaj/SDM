using System.Text;
using Microsoft.Extensions.Logging;

namespace SDM.Infrastructure.Logging;

/// <summary>
/// Appends log lines to a daily file. Writes are synchronous and flushed immediately:
/// the entries that matter most are the ones written just before a crash, and a
/// background queue would be holding exactly those in memory when the process died.
/// </summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly Lock _sync = new();
    private readonly FileLogOptions _options;

    private StreamWriter? _writer;
    private DateOnly _day;
    private int _rollIndex;
    private long _written;
    private bool _disposed;

    public FileLogWriter(FileLogOptions options)
    {
        _options = options;
        Directory = SdmLogPaths.ResolveDirectory(options.DirectoryPath);

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            RemoveExpiredFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Starting without a log file is survivable; failing to start is not.
        }
    }

    public string Directory { get; }

    public LogLevel MinimumLevel => _options.MinimumLevel;

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        string line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {Abbreviate(level)} {category}: {message}");

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                StreamWriter writer = EnsureWriter();
                writer.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;

                if (exception is not null)
                {
                    string detail = exception.ToString();
                    writer.WriteLine(detail);
                    _written += detail.Length + Environment.NewLine.Length;
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the reason a download fails.
            }
        }
    }

    private StreamWriter EnsureWriter()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);

        if (_writer is not null && today == _day && _written < _options.MaximumFileBytes)
        {
            return _writer;
        }

        if (today != _day)
        {
            _rollIndex = 0;
        }
        else if (_writer is not null)
        {
            _rollIndex++;
        }

        _writer?.Dispose();
        _day = today;
        _written = 0;

        string path = Path.Combine(Directory, FileNameFor(today, _rollIndex));
        System.IO.Directory.CreateDirectory(Directory);

        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };

        _written = stream.Length;
        return _writer;
    }

    private static string FileNameFor(DateOnly day, int rollIndex) =>
        rollIndex == 0
            ? $"sdm-{day:yyyyMMdd}.log"
            : $"sdm-{day:yyyyMMdd}-{rollIndex}.log";

    private void RemoveExpiredFiles()
    {
        DateTime cutoff = DateTime.Now.AddDays(-_options.RetainedDays);

        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "sdm-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that will not go away is not worth failing startup over.
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "———",
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}

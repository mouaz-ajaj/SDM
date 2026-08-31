using Microsoft.Extensions.Logging;

namespace SDM.Infrastructure.Logging;

[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter _writer;

    public FileLoggerProvider(FileLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _writer = new FileLogWriter(options);
    }

    /// <summary>Where the log files are being written, for showing the user.</summary>
    public string Directory => _writer.Directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(string category, FileLogWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= writer.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (IsEnabled(logLevel))
            {
                writer.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}

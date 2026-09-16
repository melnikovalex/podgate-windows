namespace PodGate.Service;

/// <summary>
/// Minimal file log: one timestamped line per event, the same shape as the app log.
/// </summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message, Exception? exception)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}  [{Short(level)}] {message}";
        if (exception is not null) line += $" :: {exception.GetType().Name}: {exception.Message}";

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, [line]);
            }
            catch (IOException)
            {
                // Logging must never take the service down.
            }
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "dbg",
        LogLevel.Information => "inf",
        LogLevel.Warning => "WRN",
        LogLevel.Error or LogLevel.Critical => "ERR",
        _ => "   ",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

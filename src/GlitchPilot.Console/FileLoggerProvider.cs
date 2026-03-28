using Microsoft.Extensions.Logging;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public FileLoggerProvider(string path)
    {
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger : ILogger
    {
        private readonly StreamWriter _writer;
        private readonly string _category;

        public FileLogger(StreamWriter writer, string category)
        {
            _writer = writer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:HH:mm:ss} [{logLevel}] {_category}: {formatter(state, exception)}";
            lock (_writer)
            {
                _writer.WriteLine(line);
                if (exception is not null)
                    _writer.WriteLine(exception.ToString());
            }
        }
    }
}

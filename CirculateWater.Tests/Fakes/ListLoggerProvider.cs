using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace CirculateWater.Tests.Fakes;

internal sealed record LogEntry(LogLevel Level, string Message, Exception Exception);

/// <summary>
/// Captures log entries so tests can assert on them.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. entries];

    public ILogger CreateLogger(string categoryName) => new ListLogger(entries);

    public void Dispose()
    {
    }

    private sealed class ListLogger(ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

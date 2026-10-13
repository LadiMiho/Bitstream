using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Bitstream.Api.Tests.Logging;

/// <summary>Keeps every log entry written through it, for assertions on what a host logged.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<(string Category, LogLevel Level, string Message)> Entries { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Add((category, logLevel, formatter(state, exception)));
    }
}

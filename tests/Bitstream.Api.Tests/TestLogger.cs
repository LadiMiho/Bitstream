using Microsoft.Extensions.Logging;

namespace Bitstream.Api.Tests;

/// <summary>A recorded log entry.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">Formatted message.</param>
/// <param name="State">State object, so structured fields can be asserted on.</param>
/// <param name="Exception">Exception, when one was logged.</param>
public sealed record LogEntry(LogLevel Level, string Message, object? State, Exception? Exception)
{
    /// <summary>Reads a structured field by name, or null when the entry has no such field.</summary>
    /// <param name="name">Field name as it appears in the message template.</param>
    public object? Field(string name) =>
        State is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.FirstOrDefault(pair => pair.Key == name).Value
            : null;
}

/// <summary>
/// Logger that records what it is asked to write.
/// <para>
/// The middleware's job is to produce specific structured output — status, duration, level —
/// so the tests assert on the entries themselves rather than on a mock's call count.
/// </para>
/// </summary>
/// <typeparam name="T">Category type.</typeparam>
public sealed class TestLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];

    /// <summary>Entries recorded so far, in order.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Scopes that were opened, so scope contents can be asserted on.</summary>
    public List<object> Scopes { get; } = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        Scopes.Add(state);
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        _entries.Add(new LogEntry(logLevel, formatter(state, exception), state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
            // Nothing to release; the scope exists only so the contents can be inspected.
        }
    }
}

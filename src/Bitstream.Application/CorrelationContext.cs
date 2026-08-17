using Bitstream.Application.Abstractions;

namespace Bitstream.Application;

/// <summary>
/// <see cref="ICorrelationContext"/> backed by <see cref="AsyncLocal{T}"/>.
/// <para>
/// Deliberately not tied to <c>HttpContext</c>: the outbox dispatcher, the BI synchronisation
/// job and the auto-confirmation sweep all need a correlation ID, and none of them has a
/// request. A background job opens a scope the same way the middleware does.
/// </para>
/// <para>
/// Registered as a singleton — the value it holds is per-async-flow, not per-instance.
/// </para>
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <inheritdoc />
    public string CorrelationId => Current.Value ??= NewCorrelationId();

    /// <inheritdoc />
    public IDisposable BeginScope(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var previous = Current.Value;
        Current.Value = correlationId;

        return new Scope(previous);
    }

    /// <summary>Generates a correlation ID: 32 lowercase hex characters, matching a W3C trace ID.</summary>
    public static string NewCorrelationId() => Guid.NewGuid().ToString("n");

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Current.Value = _previous;
            _disposed = true;
        }
    }
}

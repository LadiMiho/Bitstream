namespace Bitstream.Application.Abstractions;

/// <summary>
/// Transport names for the correlation ID. Declared once, in the layer both the presentation
/// and the integration layer can see, so that the inbound header and the outbound header
/// cannot drift apart (TR-ARC-04).
/// </summary>
public static class CorrelationHeaders
{
    /// <summary>HTTP header carrying the correlation ID in both directions.</summary>
    public const string Name = "X-Correlation-Id";
}

/// <summary>
/// Ambient correlation ID for the current logical operation, TR-ARC-04.
/// <para>
/// The presentation layer sets it once per request; application services and adapters read it
/// so that the same value reaches every log entry (TR-NFR-15), every outbound integration
/// message (TR-INT-02) and every audit record (TR-SEC-23). Background work — the outbox
/// dispatcher, the auto-confirmation sweep — sets it too, so a job-originated call is as
/// traceable as a user-originated one.
/// </para>
/// </summary>
public interface ICorrelationContext
{
    /// <summary>Correlation ID of the current operation. Never null; a value is generated on first read.</summary>
    string CorrelationId { get; }

    /// <summary>
    /// Sets the correlation ID for the current async flow and everything it starts.
    /// Disposing the returned scope restores the previous value.
    /// </summary>
    /// <param name="correlationId">Value to adopt, typically from an inbound header.</param>
    IDisposable BeginScope(string correlationId);
}

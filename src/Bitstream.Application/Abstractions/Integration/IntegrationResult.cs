namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Outcome classification every adapter must return. The distinction matters because
/// TR-INT-19 forbids retrying a business rejection while TR-INT-20 requires retrying a
/// technical failure, and only the adapter can tell the two apart from a vendor response.
/// </summary>
public enum IntegrationOutcome
{
    /// <summary>The target system accepted the message.</summary>
    Succeeded,

    /// <summary>The target system rejected the message on business grounds. Must not be retried (TR-INT-19).</summary>
    BusinessRejection,

    /// <summary>Transport, protocol or server-side failure. Retryable per TR-INT-04.</summary>
    TechnicalFailure,

    /// <summary>No response within the configured timeout. Follow with a status query or an idempotent retry, never a blind re-create (TR-INT-20).</summary>
    Timeout
}

/// <summary>Result of a single adapter call.</summary>
/// <typeparam name="TValue">Payload returned by the target system.</typeparam>
public sealed record IntegrationResult<TValue>
{
    private IntegrationResult(IntegrationOutcome outcome, TValue? value, string? errorCode, string? errorMessage)
    {
        Outcome = outcome;
        Value = value;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public IntegrationOutcome Outcome { get; }

    public TValue? Value { get; }

    /// <summary>Vendor error code, recorded against the integration message.</summary>
    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public bool IsSuccess => Outcome == IntegrationOutcome.Succeeded;

    /// <summary>True when the caller may schedule a retry (TR-INT-04, TR-INT-19).</summary>
    public bool IsRetryable =>
        Outcome is IntegrationOutcome.TechnicalFailure or IntegrationOutcome.Timeout;

    public static IntegrationResult<TValue> Success(TValue value) =>
        new(IntegrationOutcome.Succeeded, value, null, null);

    public static IntegrationResult<TValue> BusinessRejection(string errorCode, string errorMessage) =>
        new(IntegrationOutcome.BusinessRejection, default, errorCode, errorMessage);

    public static IntegrationResult<TValue> TechnicalFailure(string errorMessage, string? errorCode = null) =>
        new(IntegrationOutcome.TechnicalFailure, default, errorCode, errorMessage);

    public static IntegrationResult<TValue> Timeout(string errorMessage) =>
        new(IntegrationOutcome.Timeout, default, null, errorMessage);
}

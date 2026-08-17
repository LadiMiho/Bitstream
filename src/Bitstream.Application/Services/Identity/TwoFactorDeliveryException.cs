namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Thrown when a generated one-time code (<c>EmailOtp</c>, <c>SmsOtp</c>) could not be
/// dispatched. The presentation layer maps this to a response telling the user to retry rather
/// than to a generic 500 — login is blocked either way, but the cause is worth distinguishing
/// from an unrelated server fault.
/// </summary>
public sealed class TwoFactorDeliveryException : Exception
{
    public TwoFactorDeliveryException(string message)
        : base(message)
    {
    }

    public TwoFactorDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

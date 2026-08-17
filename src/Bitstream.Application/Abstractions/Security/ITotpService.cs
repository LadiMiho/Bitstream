namespace Bitstream.Application.Abstractions.Security;

/// <summary>
/// Time-based one-time codes, RFC 6238, for the <c>Totp</c> second-factor channel (TR-SEC-04).
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a fresh random secret for a user enrolling in TOTP.</summary>
    byte[] GenerateSecret();

    /// <summary>
    /// The code an authenticator app would show right now for <paramref name="secret"/>. Used
    /// only to build the provisioning URI shown once at enrolment; never used to check a
    /// submitted code, which must go through <see cref="Validate"/> so that clock skew is
    /// tolerated.
    /// </summary>
    string GenerateCurrentCode(byte[] secret);

    /// <summary>
    /// Validates a submitted code against <paramref name="secret"/>, tolerating the configured
    /// clock-skew window either side of the current step.
    /// </summary>
    bool Validate(byte[] secret, string code);

    /// <summary>
    /// A <c>otpauth://</c> URI encoding <paramref name="secret"/>, suitable for a QR code or
    /// manual entry into an authenticator app.
    /// </summary>
    string BuildProvisioningUri(byte[] secret, string accountLabel, string issuer);
}

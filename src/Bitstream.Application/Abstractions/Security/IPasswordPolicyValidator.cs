namespace Bitstream.Application.Abstractions.Security;

/// <summary>Outcome of checking a candidate password against the configured policy (TR-SEC-03).</summary>
/// <param name="IsValid">True when every rule passed.</param>
/// <param name="Violations">
/// Human-readable, field-level messages, one per failed rule (TR-NFR-12: specific and
/// actionable, not a generic "invalid password").
/// </param>
public sealed record PasswordPolicyResult(bool IsValid, IReadOnlyList<string> Violations)
{
    public static PasswordPolicyResult Success { get; } = new(true, []);
}

/// <summary>
/// Password policy enforcement, TR-SEC-03: minimum length, character-class diversity, a
/// common-password denylist, and no reuse of recent passwords.
/// </summary>
public interface IPasswordPolicyValidator
{
    /// <summary>
    /// Checks a candidate password. <paramref name="recentPasswordHashes"/> is empty at first
    /// set (there is no history yet) and the last <c>PasswordHistoryCount</c> hashes otherwise.
    /// </summary>
    PasswordPolicyResult Validate(string candidatePassword, IReadOnlyList<string> recentPasswordHashes);
}

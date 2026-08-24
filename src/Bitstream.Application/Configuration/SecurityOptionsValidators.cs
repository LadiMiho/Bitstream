using Microsoft.Extensions.Options;

namespace Bitstream.Application.Configuration;

/// <summary>
/// Validates <see cref="PasswordPolicyOptions"/>. TR-SEC-02 and TR-SEC-03 are stated as fixed
/// floors, not defaults a deployment can trade away for convenience — the validator rejects a
/// configuration that would weaken them, the same way <see cref="TicketClosureOptionsValidator"/>
/// rejects a configuration that would silently disable the auto-confirmation reminders.
/// </summary>
public sealed class PasswordPolicyOptionsValidator : IValidateOptions<PasswordPolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, PasswordPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MinLength < 12)
        {
            failures.Add($"Security:PasswordPolicy:MinLength must be at least 12 (TR-SEC-03). Configured: {options.MinLength}.");
        }

        if (options.MinCharacterClasses is < 3 or > 4)
        {
            failures.Add(
                "Security:PasswordPolicy:MinCharacterClasses must be 3 or 4 — TR-SEC-03 requires at least " +
                $"three of lowercase, uppercase, digit, symbol. Configured: {options.MinCharacterClasses}.");
        }

        if (options.PasswordHistoryCount < 5)
        {
            failures.Add(
                $"Security:PasswordPolicy:PasswordHistoryCount must be at least 5 (TR-SEC-03). " +
                $"Configured: {options.PasswordHistoryCount}.");
        }

        if (options.Argon2.MemorySizeKb < 19456)
        {
            failures.Add(
                "Security:PasswordPolicy:Argon2:MemorySizeKb must be at least 19456 (19 MiB), the OWASP " +
                $"floor for Argon2id (TR-SEC-02). Configured: {options.Argon2.MemorySizeKb}.");
        }

        if (options.Argon2.Iterations < 2)
        {
            failures.Add(
                $"Security:PasswordPolicy:Argon2:Iterations must be at least 2, the OWASP floor (TR-SEC-02). " +
                $"Configured: {options.Argon2.Iterations}.");
        }

        if (options.Argon2.Parallelism < 1)
        {
            failures.Add("Security:PasswordPolicy:Argon2:Parallelism must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates <see cref="TwoFactorOptions"/>. Code length/validity/attempt-budget are Identity's own now — nothing left to bound here beyond the channel itself, which is a plain enum and needs no range check.</summary>
public sealed class TwoFactorOptionsValidator : IValidateOptions<TwoFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, TwoFactorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Validates <see cref="SessionOptions"/>.</summary>
public sealed class SessionOptionsValidator : IValidateOptions<SessionOptions>
{
    public ValidateOptionsResult Validate(string? name, SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            failures.Add("Security:Session:IdleTimeout must be greater than zero (TR-SEC-07).");
        }

        if (options.AbsoluteTimeout <= TimeSpan.Zero)
        {
            failures.Add("Security:Session:AbsoluteTimeout must be greater than zero (TR-SEC-07).");
        }

        if (options.IdleTimeout > TimeSpan.Zero && options.AbsoluteTimeout > TimeSpan.Zero &&
            options.IdleTimeout > options.AbsoluteTimeout)
        {
            failures.Add(
                "Security:Session:IdleTimeout must not exceed AbsoluteTimeout — TR-SEC-07 expires a session " +
                "at whichever limit is reached first, so an idle timeout longer than the absolute one would " +
                "never apply.");
        }

        if (string.IsNullOrWhiteSpace(options.CookieName))
        {
            failures.Add("Security:Session:CookieName must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Validates <see cref="LockoutOptions"/>.</summary>
public sealed class LockoutOptionsValidator : IValidateOptions<LockoutOptions>
{
    public ValidateOptionsResult Validate(string? name, LockoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.MaxFailedAttempts < 1
            ? ValidateOptionsResult.Fail(["Security:Lockout:MaxFailedAttempts must be at least 1 (TR-SEC-06)."])
            : ValidateOptionsResult.Success;
    }
}

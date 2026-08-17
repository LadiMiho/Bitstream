using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Identity;

/// <summary>Enforces TR-SEC-03 against a candidate password.</summary>
public sealed class PasswordPolicyValidator : IPasswordPolicyValidator
{
    private readonly IOptionsMonitor<PasswordPolicyOptions> _options;
    private readonly IPasswordHasher _passwordHasher;

    public PasswordPolicyValidator(IOptionsMonitor<PasswordPolicyOptions> options, IPasswordHasher passwordHasher)
    {
        _options = options;
        _passwordHasher = passwordHasher;
    }

    public PasswordPolicyResult Validate(string candidatePassword, IReadOnlyList<string> recentPasswordHashes)
    {
        ArgumentNullException.ThrowIfNull(candidatePassword);
        ArgumentNullException.ThrowIfNull(recentPasswordHashes);

        var options = _options.CurrentValue;
        var violations = new List<string>();

        if (candidatePassword.Length < options.MinLength)
        {
            violations.Add($"Password must be at least {options.MinLength} characters long.");
        }

        var classCount = CountCharacterClasses(candidatePassword);

        if (classCount < options.MinCharacterClasses)
        {
            violations.Add(
                $"Password must contain at least {options.MinCharacterClasses} of the following: " +
                "lowercase letters, uppercase letters, digits, symbols.");
        }

        if (CommonPasswordList.Default.Contains(candidatePassword) ||
            options.AdditionalDeniedPasswords.Contains(candidatePassword, CommonPasswordList.Comparer))
        {
            violations.Add("Password is too common. Choose a password that is not easily guessed.");
        }

        // TR-SEC-03: no reuse of the last N passwords. Each candidate is checked against every
        // stored hash rather than assuming a fixed algorithm, so a history entry created under a
        // since-superseded hashing scheme is still honoured correctly.
        if (recentPasswordHashes.Take(options.PasswordHistoryCount).Any(hash => _passwordHasher.Verify(candidatePassword, hash)))
        {
            violations.Add($"Password must not match any of your last {options.PasswordHistoryCount} passwords.");
        }

        return violations.Count == 0 ? PasswordPolicyResult.Success : new PasswordPolicyResult(false, violations);
    }

    private static int CountCharacterClasses(string password)
    {
        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSymbol = false;

        foreach (var character in password)
        {
            if (char.IsLower(character))
            {
                hasLower = true;
            }
            else if (char.IsUpper(character))
            {
                hasUpper = true;
            }
            else if (char.IsDigit(character))
            {
                hasDigit = true;
            }
            else if (!char.IsWhiteSpace(character))
            {
                hasSymbol = true;
            }
        }

        return (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
    }
}

using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Identity;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>TR-SEC-03: minimum length, character-class diversity, common-password denylist, no reuse.</summary>
public sealed class PasswordPolicyValidatorTests
{
    private readonly TestOptionsMonitor<PasswordPolicyOptions> _options = new(new PasswordPolicyOptions());
    private readonly Argon2PasswordHasher _hasher;
    private readonly PasswordPolicyValidator _validator;

    public PasswordPolicyValidatorTests()
    {
        _hasher = new Argon2PasswordHasher(_options);
        _validator = new PasswordPolicyValidator(_options, _hasher);
    }

    [Fact]
    public void Accepts_a_password_meeting_every_rule()
    {
        var result = _validator.Validate("Correct-Horse-Battery-Staple-9", []);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Rejects_a_password_shorter_than_the_configured_minimum()
    {
        var result = _validator.Validate("Ab1!Ab1!", []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Contains("12 characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_a_password_with_fewer_than_the_configured_character_classes()
    {
        // 12+ characters, lowercase only — one class.
        var result = _validator.Validate("lowercaseonly", []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Contains("lowercase letters", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("password123")]
    [InlineData("BITSTREAM123")] // denylist matching is case-insensitive
    [InlineData("qwerty123")]
    public void Rejects_a_password_on_the_common_password_denylist(string candidate)
    {
        // Several of these are also too short or too few character classes — the validator
        // reports every violation independently rather than short-circuiting, so the
        // common-password message is present regardless of what else also failed.
        var result = _validator.Validate(candidate, []);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Contains("too common", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_an_organisation_specific_denied_password_from_configuration()
    {
        var options = new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions
        {
            AdditionalDeniedPasswords = ["Wholesale-Team-2026!"]
        });
        var validator = new PasswordPolicyValidator(options, new Argon2PasswordHasher(options));

        var result = validator.Validate("Wholesale-Team-2026!", []);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_reuse_of_a_recent_password()
    {
        const string previous = "Correct-Horse-Battery-Staple-9";
        var previousHash = _hasher.Hash(previous);

        var result = _validator.Validate(previous, [previousHash]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Contains("last", StringComparison.Ordinal));
    }

    [Fact]
    public void Allows_a_password_that_does_not_match_any_recent_hash()
    {
        var previousHash = _hasher.Hash("Correct-Horse-Battery-Staple-9");

        var result = _validator.Validate("Another-Fine-Password-42", [previousHash]);

        Assert.True(result.IsValid);
    }
}

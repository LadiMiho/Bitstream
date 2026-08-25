using Bitstream.Application.Configuration;
using Bitstream.Domain.Enums;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-02 to TR-SEC-07 are stated as fixed floors or ceilings, not defaults a deployment can
/// trade away for convenience. Each validator rejects a configuration that would weaken the
/// requirement it backs, mirroring the existing TicketClosureOptionsValidatorTests pattern.
/// </summary>
public sealed class PasswordPolicyOptionsValidatorTests
{
    private readonly PasswordPolicyOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_TRD_floor_values()
    {
        Assert.True(_validator.Validate(null, new PasswordPolicyOptions()).Succeeded);
    }

    [Fact]
    public void Rejects_a_minimum_length_below_12()
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions { MinLength = 8 });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("TR-SEC-03", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_fewer_than_3_required_character_classes()
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions { MinCharacterClasses = 2 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Rejects_a_password_history_count_below_5()
    {
        var result = _validator.Validate(null, new PasswordPolicyOptions { PasswordHistoryCount = 3 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Rejects_Argon2_parameters_below_the_OWASP_floor()
    {
        Assert.True(_validator.Validate(null, new PasswordPolicyOptions
        {
            Argon2 = new Argon2Options { MemorySizeKb = 8192, Iterations = 2, Parallelism = 1 }
        }).Failed);

        Assert.True(_validator.Validate(null, new PasswordPolicyOptions
        {
            Argon2 = new Argon2Options { MemorySizeKb = 19456, Iterations = 1, Parallelism = 1 }
        }).Failed);
    }
}

public sealed class TwoFactorOptionsValidatorTests
{
    private readonly TwoFactorOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_TRD_default_values()
    {
        // Code length/validity/attempt-budget are ASP.NET Core Identity's own token providers'
        // concern now (TR-SEC-04/05) — nothing left here to bound beyond the channel itself.
        Assert.True(_validator.Validate(null, new TwoFactorOptions()).Succeeded);
    }

    [Fact]
    public void Accepts_every_configured_channel()
    {
        Assert.True(_validator.Validate(null, new TwoFactorOptions { Channel = TwoFactorChannel.Totp }).Succeeded);
        Assert.True(_validator.Validate(null, new TwoFactorOptions { Channel = TwoFactorChannel.EmailOtp }).Succeeded);
    }
}

public sealed class SessionOptionsValidatorTests
{
    private readonly SessionOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_TRD_default_values()
    {
        // TR-SEC-07: 30 minutes idle, 12 hours absolute.
        Assert.True(_validator.Validate(null, new SessionOptions()).Succeeded);
    }

    [Fact]
    public void Rejects_an_idle_timeout_longer_than_the_absolute_timeout()
    {
        // TR-SEC-07 expires a session at whichever limit is reached first; an idle timeout
        // longer than the absolute one would never apply.
        var result = _validator.Validate(null, new SessionOptions
        {
            IdleTimeout = TimeSpan.FromHours(13),
            AbsoluteTimeout = TimeSpan.FromHours(12)
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Rejects_an_empty_cookie_name()
    {
        Assert.True(_validator.Validate(null, new SessionOptions { CookieName = "" }).Failed);
    }
}

public sealed class LockoutOptionsValidatorTests
{
    private readonly LockoutOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_TRD_default_of_5()
    {
        Assert.True(_validator.Validate(null, new LockoutOptions()).Succeeded);
    }

    [Fact]
    public void Rejects_a_non_positive_threshold()
    {
        Assert.True(_validator.Validate(null, new LockoutOptions { MaxFailedAttempts = 0 }).Failed);
    }
}

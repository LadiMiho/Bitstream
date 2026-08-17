using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Identity;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>TR-SEC-04: RFC 6238 time-based one-time codes for the Totp channel.</summary>
public sealed class TotpServiceTests
{
    private readonly FakeClock _clock = new();
    private readonly TotpService _totpService;

    public TotpServiceTests() => _totpService = new TotpService(new TestOptionsMonitor<TwoFactorOptions>(new TwoFactorOptions()), _clock);

    [Fact]
    public void Validates_the_code_it_currently_generates()
    {
        var secret = _totpService.GenerateSecret();

        var code = _totpService.GenerateCurrentCode(secret);

        Assert.True(_totpService.Validate(secret, code));
    }

    [Fact]
    public void Rejects_a_code_generated_from_a_different_secret()
    {
        var secretA = _totpService.GenerateSecret();
        var secretB = _totpService.GenerateSecret();

        var codeForA = _totpService.GenerateCurrentCode(secretA);

        Assert.False(_totpService.Validate(secretB, codeForA));
    }

    [Fact]
    public void Rejects_an_arbitrary_wrong_code()
    {
        var secret = _totpService.GenerateSecret();
        var code = _totpService.GenerateCurrentCode(secret);

        // Guaranteed different by construction: shift every digit up by one, wrapping 9 to 0.
        var wrongCode = new string([.. code.Select(d => (char)('0' + ((d - '0' + 1) % 10)))]);

        Assert.False(_totpService.Validate(secret, wrongCode));
    }

    [Fact]
    public void Rejects_an_empty_or_whitespace_code()
    {
        var secret = _totpService.GenerateSecret();

        Assert.False(_totpService.Validate(secret, string.Empty));
        Assert.False(_totpService.Validate(secret, "   "));
    }

    [Fact]
    public void Produces_six_digit_codes_by_default()
    {
        var secret = _totpService.GenerateSecret();

        var code = _totpService.GenerateCurrentCode(secret);

        Assert.Equal(6, code.Length);
        Assert.True(code.All(char.IsDigit));
    }

    [Fact]
    public void Builds_a_provisioning_URI_carrying_the_issuer_and_account_label()
    {
        var secret = _totpService.GenerateSecret();

        var uri = _totpService.BuildProvisioningUri(secret, "isp-user@example.com", "Bitstream Portal");

        Assert.StartsWith("otpauth://totp/", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Bitstream%20Portal", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_code_once_it_has_moved_outside_the_tolerated_clock_skew()
    {
        // Zero tolerance: only the current step's code is accepted. The clock is a fake, so the
        // step boundary is crossed deterministically rather than by sleeping the test thread.
        var clock = new FakeClock { UtcNow = DateTimeOffset.UnixEpoch };
        var strictService = new TotpService(
            new TestOptionsMonitor<TwoFactorOptions>(new TwoFactorOptions { TotpAllowedSkewSteps = 0, TotpStepSeconds = 30 }),
            clock);

        var secret = strictService.GenerateSecret();
        var codeAtFirstStep = strictService.GenerateCurrentCode(secret);
        Assert.True(strictService.Validate(secret, codeAtFirstStep));

        clock.UtcNow += TimeSpan.FromSeconds(90); // three steps later

        Assert.False(strictService.Validate(secret, codeAtFirstStep));
    }
}

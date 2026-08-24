using Bitstream.Application.Services.Identity;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// The "encrypted" half of <see cref="Bitstream.Application.Identity.Entities.User.TotpSecret"/> (TR-SEC-04).
/// Keyed through <see cref="Bitstream.Application.Abstractions.Configuration.ISecretResolver"/>
/// (TR-SEC-28) rather than a value handed to the constructor directly, so these tests exercise
/// exactly the same resolution path production does, just against a fake store.
/// </summary>
public sealed class AesGcmTotpSecretProtectorTests
{
    private static readonly byte[] ThirtyTwoByteKey = Convert.FromHexString(new string('a', 64));
    private static readonly string ValidKeyBase64 = Convert.ToBase64String(ThirtyTwoByteKey);

    [Fact]
    public async Task Unprotects_back_to_the_original_secret()
    {
        var resolver = new FakeSecretResolver().Set(AesGcmTotpSecretProtector.SecretName, ValidKeyBase64);
        var protector = new AesGcmTotpSecretProtector(resolver);
        var secret = new TotpService(new TestOptionsMonitor<Bitstream.Application.Configuration.TwoFactorOptions>(
            new Bitstream.Application.Configuration.TwoFactorOptions()), new FakeClock()).GenerateSecret();

        var protectedSecret = await protector.ProtectAsync(secret);
        var recovered = await protector.UnprotectAsync(protectedSecret);

        Assert.Equal(secret, recovered);
    }

    [Fact]
    public async Task Produces_different_ciphertext_each_time_for_the_same_secret()
    {
        // A fresh random nonce per call — reusing a nonce with the same key breaks AES-GCM's
        // confidentiality guarantee.
        var resolver = new FakeSecretResolver().Set(AesGcmTotpSecretProtector.SecretName, ValidKeyBase64);
        var protector = new AesGcmTotpSecretProtector(resolver);
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        var first = await protector.ProtectAsync(secret);
        var second = await protector.ProtectAsync(secret);

        Assert.NotEqual(first, second);
        Assert.Equal(secret, await protector.UnprotectAsync(first));
        Assert.Equal(secret, await protector.UnprotectAsync(second));
    }

    [Fact]
    public async Task Fails_closed_when_the_key_is_missing()
    {
        var resolver = new FakeSecretResolver(); // TotpEncryptionKey not set
        var protector = new AesGcmTotpSecretProtector(resolver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.ProtectAsync([1, 2, 3]));
    }

    [Fact]
    public async Task Fails_closed_when_the_key_is_not_32_bytes()
    {
        var resolver = new FakeSecretResolver().Set(AesGcmTotpSecretProtector.SecretName, Convert.ToBase64String([1, 2, 3]));
        var protector = new AesGcmTotpSecretProtector(resolver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.ProtectAsync([1, 2, 3]));
    }

    [Fact]
    public async Task Fails_closed_when_the_key_is_not_valid_base64()
    {
        var resolver = new FakeSecretResolver().Set(AesGcmTotpSecretProtector.SecretName, "not-base64-!!!");
        var protector = new AesGcmTotpSecretProtector(resolver);

        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.ProtectAsync([1, 2, 3]));
    }
}

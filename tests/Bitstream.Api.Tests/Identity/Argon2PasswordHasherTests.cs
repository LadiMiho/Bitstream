using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Identity;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>TR-SEC-02: salted, adaptive, one-way. Reversible storage is prohibited — there is no "unhash" to test.</summary>
public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public void Verifies_the_password_it_was_created_from()
    {
        var hasher = new Argon2PasswordHasher(new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions()));

        var hash = hasher.Hash("Correct-Horse-Battery-Staple-9");

        Assert.True(hasher.Verify("Correct-Horse-Battery-Staple-9", hash));
    }

    [Fact]
    public void Rejects_a_different_password()
    {
        var hasher = new Argon2PasswordHasher(new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions()));

        var hash = hasher.Hash("Correct-Horse-Battery-Staple-9");

        Assert.False(hasher.Verify("Wrong-Password-Entirely-1", hash));
    }

    [Fact]
    public void Produces_a_different_hash_each_time_for_the_same_password()
    {
        // A fresh random salt every call — otherwise two users with the same password would
        // have identical stored hashes, which leaks that fact to anyone with database access.
        var hasher = new Argon2PasswordHasher(new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions()));

        var first = hasher.Hash("Correct-Horse-Battery-Staple-9");
        var second = hasher.Hash("Correct-Horse-Battery-Staple-9");

        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify("Correct-Horse-Battery-Staple-9", first));
        Assert.True(hasher.Verify("Correct-Horse-Battery-Staple-9", second));
    }

    [Fact]
    public void Embeds_the_algorithm_tag_and_cost_parameters_in_the_stored_hash()
    {
        var hasher = new Argon2PasswordHasher(new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions()));

        var hash = hasher.Hash("Correct-Horse-Battery-Staple-9");

        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Flags_a_hash_created_under_weaker_parameters_as_needing_rehash()
    {
        var weakOptions = new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions
        {
            Argon2 = new Argon2Options { MemorySizeKb = 19456, Iterations = 2, Parallelism = 1 }
        });
        var weakHasher = new Argon2PasswordHasher(weakOptions);
        var hash = weakHasher.Hash("Correct-Horse-Battery-Staple-9");

        var strongerOptions = new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions
        {
            Argon2 = new Argon2Options { MemorySizeKb = 65536, Iterations = 3, Parallelism = 1 }
        });
        var strongerHasher = new Argon2PasswordHasher(strongerOptions);

        Assert.True(strongerHasher.NeedsRehash(hash));
        Assert.False(weakHasher.NeedsRehash(hash));
    }

    [Fact]
    public void Does_not_need_rehash_for_a_hash_already_at_or_above_the_current_cost()
    {
        var hasher = new Argon2PasswordHasher(new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions()));

        var hash = hasher.Hash("Correct-Horse-Battery-Staple-9");

        Assert.False(hasher.NeedsRehash(hash));
    }
}

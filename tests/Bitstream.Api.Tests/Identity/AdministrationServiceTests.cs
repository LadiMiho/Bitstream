using Bitstream.Application.Configuration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>TRD 4.2 (TR-SEC-09 to TR-SEC-16). Unit-tested against hand-written fakes rather than a real database.</summary>
public sealed class AdministrationServiceTests
{
    private readonly FakeIspRepository _ispRepository = new();
    private readonly FakeUserStore _userRepository = new();
    private readonly FakeRoleStore _roleStore = new();
    private readonly FakeAuditWriter _auditWriter = new();
    private readonly FakeCurrentUserContext _currentUser = new() { UserId = 1, RoleName = "Administrator" };
    private readonly FakeClock _clock = new();

    private UserManager<User> _userManager = null!;

    private AdministrationService CreateService()
    {
        _roleStore.Roles["IspUser"] = new Role { Id = 10, Name = "IspUser", IsSystemRole = true };
        _roleStore.Roles["Administrator"] = new Role { Id = 20, Name = "Administrator", IsSystemRole = true };

        var passwordPolicyOptions = new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions());
        var passwordHasher = new Argon2PasswordHasher(passwordPolicyOptions);
        var identityPasswordHasher = new Argon2IdentityPasswordHasher(passwordHasher);
        _userManager = TestIdentityFactory.CreateUserManager(_userRepository, identityPasswordHasher);
        var roleManager = TestIdentityFactory.CreateRoleManager(_roleStore);

        return new AdministrationService(
            _ispRepository,
            _userRepository,
            _userManager,
            roleManager,
            passwordHasher,
            new PasswordPolicyValidator(passwordPolicyOptions, passwordHasher),
            new FakeUnitOfWork(),
            _auditWriter,
            _clock,
            _currentUser,
            passwordPolicyOptions);
    }

    [Fact]
    public async Task CreateIspAsync_rejects_a_duplicate_NIPT()
    {
        var service = CreateService();
        _ispRepository.Isps[1] = new Isp
        {
            IspId = 1,
            Name = "Existing",
            Nipt = "L12345678A",
            ContactPerson = "A",
            ContactEmail = "a@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP1"
        };

        var exception = await Assert.ThrowsAsync<AdministrationValidationException>(() => service.CreateIspAsync(
            new CreateIspRequest("New ISP", "L12345678A", "B", "b@example.com", "+355697654321", "BP2")));

        Assert.Contains(exception.Violations, v => v.Contains("NIPT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateIspAsync_rejects_an_invalid_mobile_number()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<AdministrationValidationException>(() => service.CreateIspAsync(
            new CreateIspRequest("New ISP", "L12345678A", "B", "b@example.com", "0691234567", "BP2")));

        Assert.Contains(exception.Violations, v => v.Contains("E.164", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateUserAsync_rejects_a_password_that_fails_policy()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<AdministrationValidationException>(() => service.CreateUserAsync(
            new CreateUserRequest(null, "Jane Doe", "jane@example.com", "+355691234567", "IspUser", "short")));

        Assert.Contains(exception.Violations, v => v.Contains("12 characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateUserAsync_succeeds_with_a_policy_compliant_password_and_sets_the_role()
    {
        var service = CreateService();

        var user = await service.CreateUserAsync(
            new CreateUserRequest(null, "Jane Doe", "jane@example.com", "+355691234567", "IspUser", "Correct-Horse-Battery-Staple-9"));

        Assert.Equal("IspUser", user.Role.Name);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.True(user.TwoFactorEnabled);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "User.Created");
    }

    [Fact]
    public async Task SetIspStatusAsync_locking_cascades_to_active_users_and_rotates_their_security_stamp()
    {
        var service = CreateService();

        _ispRepository.Isps[1] = new Isp
        {
            IspId = 1,
            Name = "Alpha",
            Nipt = "L1",
            ContactPerson = "A",
            ContactEmail = "a@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP1"
        };
        _userRepository.Users[100] = MakeUser(100, ispId: 1);
        _userRepository.Users[101] = MakeUser(101, ispId: 1);
        // Already locked for an unrelated reason — must not appear in the cascade's audit trail
        // a second time, and must not be "unlocked" by this operation.
        _userRepository.Users[102] = MakeUser(102, ispId: 1, locked: true);
        // A different ISP entirely — must be untouched.
        _userRepository.Users[200] = MakeUser(200, ispId: 2);

        var stampBefore100 = _userRepository.Users[100].SecurityStamp;

        await service.SetIspStatusAsync(1, IspStatus.Locked);

        Assert.Equal(IspStatus.Locked, _ispRepository.Isps[1].Status);
        Assert.True(await _userManager.IsLockedOutAsync(_userRepository.Users[100]));
        Assert.True(await _userManager.IsLockedOutAsync(_userRepository.Users[101]));
        Assert.False(await _userManager.IsLockedOutAsync(_userRepository.Users[200]));

        // TR-SEC-07: the security stamp rotates as part of the same operation, invalidating any
        // existing cookie immediately (checked every request in the real pipeline).
        Assert.NotEqual(stampBefore100, _userRepository.Users[100].SecurityStamp);

        // TR-SEC-22: the cascade is audited per affected user, not just as one ISP-level entry.
        Assert.Equal(2, _auditWriter.Entries.Count(e => e.ActionCode == "User.StatusChanged"));
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Isp.StatusChanged");
    }

    [Fact]
    public async Task SetIspStatusAsync_unlocking_does_not_reciprocally_unlock_its_users()
    {
        var service = CreateService();

        _ispRepository.Isps[1] = new Isp
        {
            IspId = 1,
            Name = "Alpha",
            Nipt = "L1",
            ContactPerson = "A",
            ContactEmail = "a@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP1",
            Status = IspStatus.Locked
        };
        _userRepository.Users[100] = MakeUser(100, ispId: 1, locked: true);

        await service.SetIspStatusAsync(1, IspStatus.Active);

        Assert.Equal(IspStatus.Active, _ispRepository.Isps[1].Status);
        // Still locked: an administrator must unlock the user explicitly.
        Assert.True(await _userManager.IsLockedOutAsync(_userRepository.Users[100]));
    }

    [Fact]
    public async Task SetUserLockedAsync_locking_rotates_the_users_security_stamp()
    {
        var service = CreateService();
        _userRepository.Users[100] = MakeUser(100, ispId: 1);
        var stampBefore = _userRepository.Users[100].SecurityStamp;

        await service.SetUserLockedAsync(100, locked: true);

        Assert.True(await _userManager.IsLockedOutAsync(_userRepository.Users[100]));
        Assert.NotEqual(stampBefore, _userRepository.Users[100].SecurityStamp);
    }

    [Fact]
    public async Task GetIspAsync_returns_null_for_an_ISP_user_requesting_a_different_ISP()
    {
        // TR-SEC-19, unit-tested at the source of the decision (see also
        // CrossIspAccessTests for the same rule proven through the real HTTP pipeline).
        var service = CreateService();
        _currentUser.UserId = 5;
        _currentUser.IspId = 1;
        _currentUser.RoleName = "IspUser";
        _ispRepository.Isps[2] = new Isp
        {
            IspId = 2,
            Name = "Beta",
            Nipt = "L2",
            ContactPerson = "B",
            ContactEmail = "b@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP2"
        };

        var result = await service.GetIspAsync(2);

        Assert.Null(result);
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "Security.AccessDenied.CrossIsp");
    }

    [Fact]
    public async Task GetIspAsync_returns_the_ISP_for_its_own_user()
    {
        var service = CreateService();
        _currentUser.UserId = 5;
        _currentUser.IspId = 1;
        _currentUser.RoleName = "IspUser";
        _ispRepository.Isps[1] = new Isp
        {
            IspId = 1,
            Name = "Alpha",
            Nipt = "L1",
            ContactPerson = "A",
            ContactEmail = "a@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = "BP1"
        };

        var result = await service.GetIspAsync(1);

        Assert.NotNull(result);
        Assert.Equal("Alpha", result.Name);
    }

    private static User MakeUser(long userId, long ispId, bool locked = false) => new()
    {
        Id = userId,
        IspId = ispId,
        FullName = "Test User",
        Email = $"user{userId}@example.com",
        Mobile = "+355691234567",
        RoleId = 10,
        Status = UserStatus.Active,
        PasswordHash = "irrelevant",
        PasswordHashAlgorithm = "Argon2id",
        SecurityStamp = Guid.NewGuid().ToString(),
        LockoutEnabled = true,
        LockoutEnd = locked ? DateTimeOffset.MaxValue : null,
        CreatedAt = DateTimeOffset.UtcNow
    };
}

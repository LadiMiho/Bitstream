using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TRD 4.2 (TR-SEC-09 to TR-SEC-16). Unit-tested against hand-written fakes rather than a real
/// database: <see cref="AdministrationService.SetIspStatusAsync"/> and
/// <see cref="AdministrationService.SetUserStatusAsync"/> call
/// <c>IUserSessionStore</c>'s bulk revoke methods, which use EF Core's <c>ExecuteUpdateAsync</c>
/// — unsupported by the InMemory provider the HTTP-level tests use, so this is the only way to
/// exercise the lock cascade in this environment (see Fakes.cs).
/// </summary>
public sealed class AdministrationServiceTests
{
    private readonly FakeIspRepository _ispRepository = new();
    private readonly FakeUserRepository _userRepository = new();
    private readonly FakeRoleRepository _roleRepository = new();
    private readonly FakeUserSessionStore _sessionStore = new();
    private readonly FakeAuditWriter _auditWriter = new();
    private readonly FakeCurrentUserContext _currentUser = new() { UserId = 1, RoleName = "Administrator" };
    private readonly FakeClock _clock = new();

    private AdministrationService CreateService()
    {
        _roleRepository.Roles["IspUser"] = new Role { RoleId = 10, Name = "IspUser", IsSystemRole = true };
        _roleRepository.Roles["Administrator"] = new Role { RoleId = 20, Name = "Administrator", IsSystemRole = true };

        var passwordPolicyOptions = new TestOptionsMonitor<PasswordPolicyOptions>(new PasswordPolicyOptions());
        var passwordHasher = new Argon2PasswordHasher(passwordPolicyOptions);

        // EmailOtp, not Totp: proves ThrowingTotpService/ThrowingTotpSecretProtector are safe to
        // pass in — CreateUserAsync's Totp provisioning branch must not run under this channel.
        var twoFactorOptions = new TestOptionsMonitor<TwoFactorOptions>(new TwoFactorOptions { Channel = TwoFactorChannel.EmailOtp });

        return new AdministrationService(
            _ispRepository,
            _userRepository,
            _roleRepository,
            _sessionStore,
            passwordHasher,
            new PasswordPolicyValidator(passwordPolicyOptions, passwordHasher),
            new ThrowingTotpService(),
            new ThrowingTotpSecretProtector(),
            new FakeUnitOfWork(),
            _auditWriter,
            _clock,
            _currentUser,
            twoFactorOptions);
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
        Assert.Contains(_auditWriter.Entries, e => e.ActionCode == "User.Created");
    }

    [Fact]
    public async Task SetIspStatusAsync_locking_cascades_to_active_users_and_revokes_their_sessions()
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
        _userRepository.Users[100] = MakeUser(100, ispId: 1, status: UserStatus.Active);
        _userRepository.Users[101] = MakeUser(101, ispId: 1, status: UserStatus.Active);
        // Already locked for an unrelated reason — must not appear in the cascade's audit trail
        // a second time, and must not be "unlocked" by this operation.
        _userRepository.Users[102] = MakeUser(102, ispId: 1, status: UserStatus.Locked);
        // A different ISP entirely — must be untouched.
        _userRepository.Users[200] = MakeUser(200, ispId: 2, status: UserStatus.Active);

        await service.SetIspStatusAsync(1, IspStatus.Locked);

        Assert.Equal(IspStatus.Locked, _ispRepository.Isps[1].Status);
        Assert.Equal(UserStatus.Locked, _userRepository.Users[100].Status);
        Assert.Equal(UserStatus.Locked, _userRepository.Users[101].Status);
        Assert.Equal(UserStatus.Active, _userRepository.Users[200].Status);

        // TR-SEC-07: sessions revoked as part of the same operation.
        Assert.Single(_sessionStore.IspRevocations, revocation => revocation.IspId == 1 && revocation.Reason == "IspLocked");

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
        _userRepository.Users[100] = MakeUser(100, ispId: 1, status: UserStatus.Locked);

        await service.SetIspStatusAsync(1, IspStatus.Active);

        Assert.Equal(IspStatus.Active, _ispRepository.Isps[1].Status);
        // Still locked: an administrator must unlock the user explicitly.
        Assert.Equal(UserStatus.Locked, _userRepository.Users[100].Status);
        Assert.Empty(_sessionStore.IspRevocations);
    }

    [Fact]
    public async Task SetUserStatusAsync_locking_revokes_the_users_sessions()
    {
        var service = CreateService();
        _userRepository.Users[100] = MakeUser(100, ispId: 1, status: UserStatus.Active);

        await service.SetUserStatusAsync(100, UserStatus.Locked);

        Assert.Equal(UserStatus.Locked, _userRepository.Users[100].Status);
        Assert.Single(_sessionStore.UserRevocations, revocation => revocation.UserId == 100 && revocation.Reason == "UserLocked");
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

    private static User MakeUser(long userId, long ispId, UserStatus status) => new()
    {
        UserId = userId,
        IspId = ispId,
        FullName = "Test User",
        Email = $"user{userId}@example.com",
        Mobile = "+355691234567",
        RoleId = 10,
        Status = status,
        PasswordHash = "irrelevant",
        PasswordHashAlgorithm = "Argon2id",
        CreatedAt = DateTimeOffset.UtcNow
    };
}

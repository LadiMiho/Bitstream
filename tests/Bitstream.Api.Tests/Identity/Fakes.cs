using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Tests.Identity;

/*
 * Hand-written test doubles for the application-layer ports, matching the style already used
 * by TestLogger in this project rather than adding a mocking framework.
 */

/// <summary>Fixed value <see cref="IOptionsMonitor{TOptions}"/>, for constructing a service under test with one known configuration.</summary>
public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    public TestOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FakeCurrentUserContext : ICurrentUserContext
{
    public long? UserId { get; set; }

    public long? IspId { get; set; }

    public string? RoleName { get; set; }

    public string? ActorIp { get; set; } = "127.0.0.1";

    public string CorrelationId { get; set; } = "test-correlation-id";

    public HashSet<string> Permissions { get; } = [];

    public bool HasPermission(string permissionCode) => Permissions.Contains(permissionCode);
}

/// <summary>Records every entry it is asked to write, in order, for assertions.</summary>
public sealed class FakeAuditWriter : IAuditWriter
{
    public List<(string ActionCode, string EntityType, string? EntityId, string? OldValue, string? NewValue)> Entries { get; } = [];

    public Task WriteAsync(string actionCode, string entityType, string? entityId, string? oldValue, string? newValue, CancellationToken cancellationToken = default)
    {
        Entries.Add((actionCode, entityType, entityId, oldValue, newValue));
        return Task.CompletedTask;
    }
}

/// <summary>No-op transaction: SaveChangesAsync always "succeeds," nothing is actually persisted beyond the in-memory dictionaries the fake repositories hold directly.</summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IAsyncDisposable>(new NoopScope());

    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class NoopScope : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class FakeIspRepository : IIspRepository
{
    public Dictionary<long, Isp> Isps { get; } = [];

    public Task<Isp?> FindByIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Isps.GetValueOrDefault(ispId));

    public Task<bool> NiptExistsAsync(string nipt, CancellationToken cancellationToken = default) =>
        Task.FromResult(Isps.Values.Any(isp => isp.Nipt == nipt));

    public Task<Isp?> FindByCrmBpReferenceAsync(string crmBpReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(Isps.Values.FirstOrDefault(isp => isp.CrmBpReference == crmBpReference));

    public Task AddAsync(Isp isp, CancellationToken cancellationToken = default)
    {
        Isps[isp.IspId] = isp;
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<Isp> Items, int TotalCount)> SearchAsync(
        string? search, int skip, int take, CancellationToken cancellationToken = default)
    {
        var matches = Isps.Values
            .Where(isp => string.IsNullOrWhiteSpace(search)
                || isp.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || isp.Nipt.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(isp => isp.CreatedAt)
            .ToList();

        return Task.FromResult<(IReadOnlyList<Isp>, int)>(([.. matches.Skip(skip).Take(take)], matches.Count));
    }
}

/// <summary>
/// Doubles as <see cref="IUserRepository"/> (the narrow, password-history/ISP-cascade port that
/// remains after <c>UserManager&lt;User&gt;</c> took over core CRUD) and as the
/// <c>IUserStore&lt;User&gt;</c> family <c>UserManager&lt;User&gt;</c> itself needs — one
/// in-memory dictionary, backing both, so a test can seed and assert against
/// <see cref="Users"/> exactly as before <c>UserManager</c> existed.
/// </summary>
public sealed class FakeUserStore :
    IUserRepository, IUserStore<User>, IUserPasswordStore<User>, IUserEmailStore<User>,
    IUserTwoFactorStore<User>, IUserLockoutStore<User>, IUserSecurityStampStore<User>
{
    private long _nextUserId = 1;

    public Dictionary<long, User> Users { get; } = [];

    public Task<IReadOnlyList<User>> GetByIspIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>([.. Users.Values.Where(user => user.IspId == ispId)]);

    public Task<(IReadOnlyList<User> Items, int TotalCount)> SearchAsync(
        string? search, long? ispId, string? roleName, string? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        var matches = Users.Values
            .Where(user => user.Status != UserStatus.Deleted)
            .Where(user => ispId is null || user.IspId == ispId)
            .Where(user => string.IsNullOrWhiteSpace(search)
                || user.FullName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || user.Email!.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Where(user => string.IsNullOrEmpty(roleName) || user.Role?.Name == roleName)
            .Where(user => string.IsNullOrEmpty(status)
                || status == (user.LockoutEnd is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow ? "Locked" : "Active"))
            .OrderByDescending(user => user.CreatedAt)
            .ToList();

        return Task.FromResult<(IReadOnlyList<User>, int)>(([.. matches.Skip(skip).Take(take)], matches.Count));
    }

    public Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task AddPasswordHistoryAsync(long userId, string passwordHash, string algorithmTag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        if (userName is not null)
        {
            user.Email = userName;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email!.ToUpperInvariant());

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        if (user.Id == 0)
        {
            user.Id = _nextUserId++;
        }

        Users[user.Id] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        Users[user.Id] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Users are never physically deleted (TR-DAT-07).");

    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(long.TryParse(userId, out var id) ? Users.GetValueOrDefault(id) : null);

    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Values.FirstOrDefault(user => user.Email!.ToUpperInvariant() == normalizedUserName));

    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.PasswordHash);

    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (passwordHash is not null)
        {
            user.PasswordHash = passwordHash;
        }

        return Task.CompletedTask;
    }

    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);

    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
    {
        if (email is not null)
        {
            user.Email = email;
        }

        return Task.CompletedTask;
    }

    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(Users.Values.FirstOrDefault(user => user.Email!.ToUpperInvariant() == normalizedEmail));

    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email!.ToUpperInvariant());

    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetTwoFactorEnabledAsync(User user, bool enabled, CancellationToken cancellationToken)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.TwoFactorEnabled);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.AccessFailedCount);

    public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task SetSecurityStampAsync(User user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(User user, CancellationToken cancellationToken) => Task.FromResult(user.SecurityStamp);

    public void Dispose()
    {
    }
}

public sealed class FakeRoleStore : IRoleStore<Role>
{
    public Dictionary<string, Role> Roles { get; } = [];

    public Task<string> GetRoleIdAsync(Role role, CancellationToken cancellationToken) =>
        Task.FromResult(role.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public Task<string?> GetRoleNameAsync(Role role, CancellationToken cancellationToken) => Task.FromResult<string?>(role.Name);

    public Task SetRoleNameAsync(Role role, string? roleName, CancellationToken cancellationToken)
    {
        if (roleName is not null)
        {
            role.Name = roleName;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedRoleNameAsync(Role role, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(role.Name!.ToUpperInvariant());

    public Task SetNormalizedRoleNameAsync(Role role, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IdentityResult> CreateAsync(Role role, CancellationToken cancellationToken)
    {
        Roles[role.Name!] = role;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> UpdateAsync(Role role, CancellationToken cancellationToken)
    {
        Roles[role.Name!] = role;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(Role role, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Seeded roles are never deleted.");

    public Task<Role?> FindByIdAsync(string roleId, CancellationToken cancellationToken) =>
        Task.FromResult(long.TryParse(roleId, out var id) ? Roles.Values.FirstOrDefault(role => role.Id == id) : null);

    public Task<Role?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken) =>
        Task.FromResult(Roles.Values.FirstOrDefault(role => role.Name!.ToUpperInvariant() == normalizedRoleName));

    public void Dispose()
    {
    }
}

/// <summary>Fixed in-memory secret store, for testing components that resolve a secret by name without a real secret store.</summary>
public sealed class FakeSecretResolver : ISecretResolver
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public FakeSecretResolver Set(string name, string value)
    {
        _secrets[name] = value;
        return this;
    }

    public ValueTask<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_secrets.GetValueOrDefault(secretName));

    public ValueTask<string> GetRequiredSecretAsync(string secretName, CancellationToken cancellationToken = default) =>
        _secrets.TryGetValue(secretName, out var value)
            ? ValueTask.FromResult(value)
            : throw new InvalidOperationException($"Secret '{secretName}' is not configured in this fake.");
}

/// <summary>
/// Builds real <c>UserManager&lt;User&gt;</c>/<c>RoleManager&lt;Role&gt;</c> instances over
/// <see cref="FakeUserStore"/>/<see cref="FakeRoleStore"/> — these tests exercise Identity's own
/// classes, not a re-implementation of them, just without a database underneath.
/// </summary>
public static class TestIdentityFactory
{
    public static UserManager<User> CreateUserManager(FakeUserStore store, IPasswordHasher<User> passwordHasher) =>
        new(
            store,
            new Microsoft.Extensions.Options.OptionsWrapper<IdentityOptions>(new IdentityOptions()),
            passwordHasher,
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<User>>.Instance);

    public static RoleManager<Role> CreateRoleManager(FakeRoleStore store) =>
        new(
            store,
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleManager<Role>>.Instance);
}

using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Tests.Identity;

/*
 * Hand-written test doubles for the application-layer ports, matching the style already used
 * by TestLogger in this project rather than adding a mocking framework.
 *
 * These exist specifically for AdministrationService.SetIspStatusAsync /
 * SetUserStatusAsync: EF Core's InMemory provider (used by IdentityApiFactory for the HTTP-level
 * tests) does not support ExecuteUpdateAsync, which UserSessionStore's bulk revoke methods rely
 * on — so the lock cascade cannot be exercised through the real pipeline in this environment,
 * and is unit-tested against fakes instead.
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

    public Task AddAsync(Isp isp, CancellationToken cancellationToken = default)
    {
        Isps[isp.IspId] = isp;
        return Task.CompletedTask;
    }
}

public sealed class FakeUserRepository : IUserRepository
{
    public Dictionary<long, User> Users { get; } = [];

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Values.FirstOrDefault(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task<User?> FindByIdAsync(long userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.GetValueOrDefault(userId));

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Values.Any(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)));

    public Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        Users[user.UserId] = user;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<User>> GetByIspIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<User>>([.. Users.Values.Where(user => user.IspId == ispId)]);

    public Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task AddPasswordHistoryAsync(long userId, string passwordHash, string algorithmTag, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class FakeRoleRepository : IRoleRepository
{
    public Dictionary<string, Role> Roles { get; } = [];

    public Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Roles.GetValueOrDefault(name));
}

/// <summary>Records the arguments of every bulk revoke call, so a test can assert the cascade happened without a real database.</summary>
public sealed class FakeUserSessionStore : IUserSessionStore
{
    public List<UserSession> Sessions { get; } = [];

    public List<(long UserId, string Reason)> UserRevocations { get; } = [];

    public List<(long IspId, string Reason)> IspRevocations { get; } = [];

    public Task AddAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task<UserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sessions.FirstOrDefault(session => session.TokenHash == tokenHash));

    public Task<int> RevokeAllForUserAsync(long userId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        UserRevocations.Add((userId, reason));
        var affected = Sessions.Count(session => session.UserId == userId && session.RevokedAt is null);

        foreach (var session in Sessions.Where(session => session.UserId == userId && session.RevokedAt is null))
        {
            session.RevokedAt = revokedAt;
            session.RevokedReason = reason;
        }

        return Task.FromResult(affected);
    }

    public Task<int> RevokeAllForIspAsync(long ispId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default)
    {
        IspRevocations.Add((ispId, reason));
        return Task.FromResult(0);
    }
}

/// <summary>
/// Throws if called. Used where a test deliberately configures <c>TwoFactorOptions.Channel</c>
/// to something other than <c>Totp</c>, so the fake proves the Totp-only provisioning path was
/// not taken rather than merely being unasserted.
/// </summary>
public sealed class ThrowingTotpService : ITotpService
{
    public byte[] GenerateSecret() => throw new InvalidOperationException("Not expected to be called: the configured channel is not Totp.");

    public string GenerateCurrentCode(byte[] secret) => throw new InvalidOperationException("Not expected to be called.");

    public bool Validate(byte[] secret, string code) => throw new InvalidOperationException("Not expected to be called.");

    public string BuildProvisioningUri(byte[] secret, string accountLabel, string issuer) => throw new InvalidOperationException("Not expected to be called.");
}

/// <summary>See <see cref="ThrowingTotpService"/>.</summary>
public sealed class ThrowingTotpSecretProtector : ITotpSecretProtector
{
    public Task<byte[]> ProtectAsync(byte[] plainSecret, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Not expected to be called: the configured channel is not Totp.");

    public Task<byte[]> UnprotectAsync(byte[] protectedSecret, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Not expected to be called.");
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

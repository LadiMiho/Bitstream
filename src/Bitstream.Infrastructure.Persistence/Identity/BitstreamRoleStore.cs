using System.Globalization;
using Bitstream.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// Bridges <see cref="Role"/> to <c>RoleManager&lt;Role&gt;</c>, purely for <c>UserManager</c>
/// interop — this app never creates, renames or deletes a role through the API (the four roles
/// are seeded, db/mssql/0007_seed_roles_permissions.sql, and TR-SEC-21's configurability is
/// about permission assignment, not the role list itself). Read-only in practice; the
/// mutation methods exist only because <see cref="IRoleStore{TRole}"/> requires them.
/// </summary>
public sealed class BitstreamRoleStore : IRoleStore<Role>
{
    private readonly BitstreamDbContext _dbContext;

    public BitstreamRoleStore(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<string> GetRoleIdAsync(Role role, CancellationToken cancellationToken) =>
        Task.FromResult(role.RoleId.ToString(CultureInfo.InvariantCulture));

    public Task<string?> GetRoleNameAsync(Role role, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(role.Name);

    public Task SetRoleNameAsync(Role role, string? roleName, CancellationToken cancellationToken)
    {
        if (roleName is not null)
        {
            role.Name = roleName;
        }

        return Task.CompletedTask;
    }

    // Computed, not persisted — role lookup already works against the seeded names as-is.
    public Task<string?> GetNormalizedRoleNameAsync(Role role, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(role.Name.ToUpperInvariant());

    public Task SetNormalizedRoleNameAsync(Role role, string? normalizedName, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<IdentityResult> CreateAsync(Role role, CancellationToken cancellationToken)
    {
        await _dbContext.Roles.AddAsync(role, cancellationToken).ConfigureAwait(false);

        return IdentityResult.Success;
    }

    public Task<IdentityResult> UpdateAsync(Role role, CancellationToken cancellationToken)
    {
        _dbContext.Roles.Update(role);

        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(Role role, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Seeded roles are never deleted through this store.");

    public Task<Role?> FindByIdAsync(string roleId, CancellationToken cancellationToken) =>
        long.TryParse(roleId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? WithPermissions(_dbContext.Roles).FirstOrDefaultAsync(role => role.RoleId == id, cancellationToken)
            : Task.FromResult<Role?>(null);

    public Task<Role?> FindByNameAsync(string normalizedRoleName, CancellationToken cancellationToken) =>
        WithPermissions(_dbContext.Roles).FirstOrDefaultAsync(
            role => role.Name.ToUpper() == normalizedRoleName, cancellationToken);

    public void Dispose()
    {
        // BitstreamDbContext's lifetime is owned by DI (scoped), not by this store.
    }

    private static IQueryable<Role> WithPermissions(IQueryable<Role> query) =>
        query
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission);
}

using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IRoleRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class RoleRepository : IRoleRepository
{
    private readonly BitstreamDbContext _dbContext;

    public RoleRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
        _dbContext.Roles
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
            .FirstOrDefaultAsync(role => role.Name == name, cancellationToken);
}

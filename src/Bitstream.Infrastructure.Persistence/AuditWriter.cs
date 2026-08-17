using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Implements <see cref="IAuditWriter"/> — the only supported way to write <see cref="AuditLog"/>
/// (TR-SEC-22 to TR-SEC-24).
/// <para>
/// Saves independently rather than only tracking the entity for a later caller-driven
/// <c>SaveChangesAsync</c>. Several call sites — a cross-ISP access denial (TR-SEC-19) chief
/// among them — are pure read paths with no other state change to save alongside; if this
/// method only tracked, those audit entries would never reach the database. The cost is an
/// extra round trip on the call sites that do pair an audit entry with a business mutation;
/// durability of a security-relevant record is worth more than that round trip.
/// </para>
/// </summary>
public sealed class AuditWriter : IAuditWriter
{
    private readonly BitstreamDbContext _dbContext;
    private readonly ICurrentUserContext _currentUser;
    private readonly IClock _clock;

    public AuditWriter(BitstreamDbContext dbContext, ICurrentUserContext currentUser, IClock clock)
    {
        _dbContext = dbContext;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task WriteAsync(
        string actionCode,
        string entityType,
        string? entityId,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        _dbContext.AuditLog.Add(new AuditLog
        {
            Timestamp = _clock.UtcNow,
            ActorUserId = _currentUser.UserId,
            ActorIp = _currentUser.ActorIp,
            ActionCode = actionCode,
            EntityType = entityType,
            EntityId = entityId,
            OldValue = oldValue,
            NewValue = newValue,
            CorrelationId = _currentUser.CorrelationId
        });

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

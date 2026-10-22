using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>The Audit Logs grid's search box and Filters popup; every criterion is optional and they combine with AND.</summary>
/// <param name="Search">Case-insensitive substring of the action, the entity ID or the correlation ID.</param>
/// <param name="ActionCode">Exact action, e.g. ActivationRequest.Submitted.</param>
/// <param name="EntityType">Exact entity type, e.g. ActivationRequest.</param>
/// <param name="ActorUserId">One user's actions.</param>
/// <param name="SystemOnly">Only actions without a user (background jobs, CRM-driven changes).</param>
/// <param name="From">At or after this instant.</param>
/// <param name="Before">Before this instant (the day after the "to" date).</param>
public sealed record AuditLogSearchFilter(
    string? Search = null,
    string? ActionCode = null,
    string? EntityType = null,
    long? ActorUserId = null,
    bool SystemOnly = false,
    DateTimeOffset? From = null,
    DateTimeOffset? Before = null);

/// <summary>An audit entry with its actor's name; null for a system action or a user that no longer exists.</summary>
public sealed record AuditLogRow(AuditLog Entry, string? ActorName, string? ActorEmail);

/// <summary>A user who appears in the audit log, for the actor filter.</summary>
public sealed record AuditActor(long UserId, string Name, string Email);

/// <summary>Read side of sec.AuditLog (the write side is <see cref="IAuditWriter"/>). Never modifies a row (TR-SEC-24).</summary>
public interface IAuditLogQueries
{
    /// <summary>Newest first.</summary>
    Task<(IReadOnlyList<AuditLogRow> Items, int TotalCount)> SearchAsync(AuditLogSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    Task<AuditLogRow?> FindAsync(long auditId, CancellationToken cancellationToken = default);

    /// <summary>The distinct actions and entity types recorded, and the users who appear as actors, for the Filters popup.</summary>
    Task<(IReadOnlyList<string> ActionCodes, IReadOnlyList<string> EntityTypes, IReadOnlyList<AuditActor> Actors)> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default);
}

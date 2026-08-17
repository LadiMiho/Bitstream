using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>
/// Unit of work over the portal database. Declared here so that application services never
/// take a dependency on EF Core (TRD 2.1 layer separation).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens an explicit transaction. Required by TR-ACT-06 and TR-ARC-03: the business
    /// write, the identifier allocation and the outbox insert must commit together.
    /// </summary>
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Series a public identifier is drawn from. Activation requests and complaint tickets use
/// separate series (TR-DAT-06).
/// </summary>
public enum IdentifierSeries
{
    ActivationRequest,
    ComplaintTicket,
    ServiceChangeRequest
}

/// <summary>
/// Issues the human-readable public identifier, TRD 3.2.
/// <para>
/// The implementation must allocate from a single database counter per environment inside
/// the caller's transaction so that the series is gap-free and monotonic (TR-DAT-02b) and
/// collision-free under concurrency (TR-DAT-03). The prefix comes from environment
/// configuration (TR-DAT-02a); its value is TRD 11.4 open item 2.
/// </para>
/// </summary>
public interface IPublicIdentifierGenerator
{
    /// <summary>Returns the next identifier, e.g. <c>ISP_1024</c>.</summary>
    Task<string> NextAsync(IdentifierSeries series, CancellationToken cancellationToken = default);

    /// <summary>Validates against <c>^[A-Z]+_[0-9]+$</c> (TR-DAT-02d).</summary>
    bool IsValid(string identifier);
}

/// <summary>
/// Append-only audit writer (TR-SEC-22 to TR-SEC-24). The only supported way to write
/// <see cref="AuditLog"/>; no update or delete operation is offered by design.
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(
        string actionCode,
        string entityType,
        string? entityId,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Ambient information about the caller, resolved once per request by the presentation layer
/// and consumed by application services for ownership checks (TR-SEC-18, TR-SEC-19).
/// </summary>
public interface ICurrentUserContext
{
    long? UserId { get; }

    /// <summary>Null for internal users (Administrator, Service Desk, Auditor).</summary>
    long? IspId { get; }

    string? RoleName { get; }

    string? ActorIp { get; }

    /// <summary>Correlation ID injected at the gateway and propagated downstream (TR-ARC-04).</summary>
    string CorrelationId { get; }

    bool HasPermission(string permissionCode);
}

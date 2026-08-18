using Bitstream.Application.Abstractions;
using Bitstream.Application.Abstractions.Persistence;

namespace Bitstream.Hosting.Security;

/// <summary>
/// <see cref="ICurrentUserContext"/> for work that has no signed-in user: the outbox dispatcher,
/// the BI active-lines sync, the auto-confirmation sweep, and the CRM inbound event API — none
/// of which act on behalf of a portal user.
/// <para>
/// Every identity property is null, which is exactly what the audit log should record for a
/// system-initiated change (TR-SEC-22): attributing it to whichever user happened to be nearby
/// would be worse than recording that nobody did it. <see cref="HasPermission"/> answers false
/// for the same reason — a background job must never pass an ownership or permission check by
/// pretending to be privileged. The services these jobs call reach their write paths directly
/// rather than through the permission-scoped read paths, so this is a floor, not a limitation.
/// </para>
/// </summary>
public sealed class SystemCurrentUserContext : ICurrentUserContext
{
    private readonly ICorrelationContext _correlationContext;

    public SystemCurrentUserContext(ICorrelationContext correlationContext) =>
        _correlationContext = correlationContext;

    public long? UserId => null;

    public long? IspId => null;

    public string? RoleName => null;

    public string? ActorIp => null;

    /// <summary>The ambient correlation ID — set per inbound request, or per background pass (TR-ARC-04).</summary>
    public string CorrelationId => _correlationContext.CorrelationId;

    public bool HasPermission(string permissionCode) => false;
}

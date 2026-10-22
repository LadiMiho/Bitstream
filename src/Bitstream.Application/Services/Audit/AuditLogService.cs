using System.Globalization;
using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.Identity;

namespace Bitstream.Application.Services.Audit;

/// <summary>The caller lacks <c>audit.read</c>. The presentation layer maps this to 403.</summary>
public sealed class AuditLogAccessDeniedException : Exception
{
    public AuditLogAccessDeniedException()
        : base("Reading the audit log requires the audit.read permission.")
    {
    }
}

/// <summary>What the Audit Logs Filters popup offers.</summary>
public sealed record AuditLogFilterOptions(IReadOnlyList<string> ActionCodes, IReadOnlyList<string> EntityTypes, IReadOnlyList<AuditActor> Actors);

public interface IAuditLogService
{
    Task<PagedResult<AuditLogRow>> SearchAsync(AuditLogSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    Task<AuditLogRow?> GetAsync(long auditId, CancellationToken cancellationToken = default);

    Task<AuditLogFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry matching <paramref name="filter"/>, up to <see cref="AuditLogService.MaxExportRows"/>,
    /// for the Excel export — and the export itself is audited (TR-REP-08).
    /// </summary>
    Task<PagedResult<AuditLogRow>> ExportAsync(AuditLogSearchFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements <see cref="IAuditLogService"/>: search, view and export of sec.AuditLog (TR-SEC-25),
/// all requiring <c>audit.read</c> (Administrator, Auditor). Read-only: nothing here changes an entry.
/// </summary>
public sealed class AuditLogService : IAuditLogService
{
    public const int MaxExportRows = 50_000;

    private readonly IAuditLogQueries _queries;
    private readonly IAuditWriter _auditWriter;
    private readonly ICurrentUserContext _currentUser;

    public AuditLogService(IAuditLogQueries queries, IAuditWriter auditWriter, ICurrentUserContext currentUser)
    {
        _queries = queries;
        _auditWriter = auditWriter;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<AuditLogRow>> SearchAsync(AuditLogSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        RequireRead();
        var (items, total) = await _queries.SearchAsync(filter, skip, take, cancellationToken).ConfigureAwait(false);
        return new PagedResult<AuditLogRow>(items, total);
    }

    public Task<AuditLogRow?> GetAsync(long auditId, CancellationToken cancellationToken = default)
    {
        RequireRead();
        return _queries.FindAsync(auditId, cancellationToken);
    }

    public async Task<AuditLogFilterOptions> GetFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        RequireRead();
        var (actions, entityTypes, actors) = await _queries.GetFilterOptionsAsync(cancellationToken).ConfigureAwait(false);
        return new AuditLogFilterOptions(actions, entityTypes, actors);
    }

    public async Task<PagedResult<AuditLogRow>> ExportAsync(AuditLogSearchFilter filter, CancellationToken cancellationToken = default)
    {
        var result = await SearchAsync(filter, 0, MaxExportRows, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "AuditLog.Exported", "AuditLog", null, null,
            $"{{\"filter\":{JsonSerializer.Serialize(filter)},\"rows\":{result.Items.Count.ToString(CultureInfo.InvariantCulture)}," +
            $"\"matching\":{result.TotalCount.ToString(CultureInfo.InvariantCulture)}}}",
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private void RequireRead()
    {
        if (!_currentUser.HasPermission(PermissionCodes.AuditRead))
        {
            throw new AuditLogAccessDeniedException();
        }
    }
}

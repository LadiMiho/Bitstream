namespace Bitstream.Api.Pages.AccessManagement;

/// <summary>
/// Audit log search and export. There is currently no backend API to call: <c>IAuditWriter</c>
/// (<c>Bitstream.Infrastructure.Persistence/AuditWriter.cs</c>) only ever writes audit rows —
/// nothing reads them back — and no endpoint exists under any route despite the
/// <c>audit.read</c> permission already being seeded
/// (<c>db/mssql/0007_seed_roles_permissions.sql</c>) and granted to the Auditor and
/// Administrator roles. TR-REP-08 ("every export is audited") and the read side of TR-SEC-24
/// both depend on a search/export endpoint that has not been built yet.
/// <para>
/// Per instructions, this gap is reported rather than compensated for: this page does not call
/// a database directly, invent a client-side reconstruction of the log, or otherwise duplicate
/// what would be backend logic. It stays a placeholder describing exactly what is missing.
/// </para>
/// </summary>
public sealed class AuditLogModel : SecurePageModel
{
    public void OnGet()
    {
    }
}

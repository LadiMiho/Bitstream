using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>Access Management (TRD §4) landing page, plus the audit log placeholder screen.</summary>
[Route("AccessManagement")]
public sealed class AccessManagementController : Controller
{
    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Access Management";
        return View();
    }

    /// <summary>
    /// Audit log search and export. There is currently no backend API to call: <c>IAuditWriter</c>
    /// (<c>Bitstream.Infrastructure.Persistence/AuditWriter.cs</c>) only ever writes audit rows —
    /// nothing reads them back — and no endpoint exists under any route despite the
    /// <c>audit.read</c> permission already being seeded
    /// (<c>db/mssql/0007_seed_roles_permissions.sql</c>) and granted to the Auditor and
    /// Administrator roles. TR-REP-08 ("every export is audited") and the read side of TR-SEC-24
    /// both depend on a search/export endpoint that has not been built yet.
    /// <para>
    /// Per instructions, this gap is reported rather than compensated for: this action does not
    /// call a database directly, invent a client-side reconstruction of the log, or otherwise
    /// duplicate what would be backend logic. It stays a placeholder describing exactly what is
    /// missing.
    /// </para>
    /// </summary>
    [HttpGet("AuditLog")]
    [RequireSession]
    public IActionResult AuditLog()
    {
        ViewData["Title"] = "Audit Log";
        return View();
    }
}

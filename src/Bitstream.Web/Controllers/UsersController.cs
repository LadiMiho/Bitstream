using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Hosting.Security;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Controllers;

/// <summary>
/// User Administration: grid, plus drawer forms for add/edit/view/change password.
/// <para>
/// Every action here either renders the grid page itself or a partial view for one drawer's
/// form — it never performs a write. The actual create/update/change-password/delete/lock calls
/// still go through the existing <c>/api/v1/users</c> JSON endpoints
/// (<see cref="Bitstream.Web.Endpoints.AdministrationEndpoints"/>) from client-side script
/// (<c>wwwroot/js/pages/user-admin.js</c>), exactly as before this controller existed — nothing
/// here is a second, competing place that validates or authorises a write (TR-SEC-17).
/// </para>
/// <para>
/// The read actions (<see cref="EditDrawer"/>, <see cref="ViewDrawer"/>,
/// <see cref="ChangePasswordDrawer"/>) go through <see cref="IAdministrationService.GetUserAsync"/>,
/// which applies the same not-found-not-forbidden ownership rule as everywhere else (TR-SEC-19):
/// a user this caller is not entitled to see behaves identically to one that does not exist.
/// </para>
/// </summary>
[Route("AccessManagement/Users")]
public sealed class UsersController : Controller
{
    /// <summary>The seeded role catalogue (db/mssql/0007_seed_roles_permissions.sql) — fixed by the TRD, not something an API lists.</summary>
    private static readonly string[] Roles = ["Administrator", "IspUser", "ServiceDesk", "Auditor"];

    private readonly IAdministrationService _administrationService;

    public UsersController(IAdministrationService administrationService) => _administrationService = administrationService;

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "User Administration";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserCreate);
        ViewBag.CanEdit = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserUpdate);
        ViewBag.CanLock = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserLock);

        return View();
    }

    [HttpGet("AddDrawer")]
    [RequirePermission(PermissionCodes.UserCreate)]
    public IActionResult AddDrawer()
    {
        ViewBag.Roles = Roles;
        return PartialView("_AddDrawer");
    }

    [HttpGet("{userId:long}/EditDrawer")]
    [RequirePermission(PermissionCodes.UserUpdate)]
    public async Task<IActionResult> EditDrawer(long userId, CancellationToken cancellationToken)
    {
        var user = await _administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return NotFound();
        }

        ViewBag.Roles = Roles;
        return PartialView("_EditDrawer", user);
    }

    [HttpGet("{userId:long}/ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(long userId, CancellationToken cancellationToken)
    {
        var user = await _administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return user is null ? NotFound() : PartialView("_ViewDrawer", user);
    }

    [HttpGet("{userId:long}/ChangePasswordDrawer")]
    [RequirePermission(PermissionCodes.UserUpdate)]
    public async Task<IActionResult> ChangePasswordDrawer(long userId, CancellationToken cancellationToken)
    {
        var user = await _administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return user is null ? NotFound() : PartialView("_ChangePasswordDrawer", user);
    }
}

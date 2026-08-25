using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// User Administration: grid page, drawer forms for add/edit/view/change password, and the JSON
/// CRUD those forms submit to. This is the only place a user is created/edited/locked/deleted —
/// there is no separate "API" project consuming <see cref="IAdministrationService"/> for this;
/// <c>wwwroot/js/pages/user-admin.js</c> calls straight back into this same controller.
/// </summary>
[Route("AccessManagement/Users")]
public sealed class UsersController : Controller
{
    /// <summary>The seeded role catalogue (db/mssql/0007_seed_roles_permissions.sql) — fixed by the TRD, not something an API lists.</summary>
    private static readonly string[] Roles = ["Administrator", "IspUser", "ServiceDesk", "Auditor"];

    private readonly IAdministrationService _administrationService;
    private readonly UserManager<User> _userManager;

    public UsersController(IAdministrationService administrationService, UserManager<User> userManager)
    {
        _administrationService = administrationService;
        _userManager = userManager;
    }

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "User Administration";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserCreate);
        ViewBag.CanEdit = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserUpdate);
        ViewBag.CanLock = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserLock);
        ViewBag.Roles = Roles;

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

    // --- JSON support endpoints for the grid + drawer forms above (user-admin.js) -----------

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] string? role,
        [FromQuery] string? status,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var result = await _administrationService.SearchUsersAsync(
            search, role, status, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        var items = await Task.WhenAll(result.Items.Select(user => ToResponseAsync(user))).ConfigureAwait(false);

        return Ok(new UserListResponse(items, result.TotalCount));
    }

    [HttpPost("")]
    [RequireJsonPermission(PermissionCodes.UserCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreateUserHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _administrationService.CreateUserAsync(
                new CreateUserRequest(request.IspId, request.FullName, request.Email, request.Mobile, request.RoleName, request.InitialPassword),
                cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(Get), new { userId = user.Id }, await ToResponseAsync(user).ConfigureAwait(false));
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpGet("{userId:long}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Get(long userId, CancellationToken cancellationToken)
    {
        var user = await _administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return user is null ? NotFound() : Ok(await ToResponseAsync(user).ConfigureAwait(false));
    }

    [HttpPut("{userId:long}")]
    [RequireJsonPermission(PermissionCodes.UserUpdate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Update(long userId, [FromBody] UpdateUserHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var user = await _administrationService.UpdateUserAsync(
                userId, new UpdateUserRequest(request.IspId, request.FullName, request.Email, request.Mobile, request.RoleName),
                cancellationToken).ConfigureAwait(false);

            return Ok(await ToResponseAsync(user).ConfigureAwait(false));
        }
        catch (AdministrationValidationException exception) when (IsUserNotFound(exception, userId))
        {
            return NotFound();
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpPatch("{userId:long}/status")]
    [RequireJsonPermission(PermissionCodes.UserLock)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> SetStatus(long userId, [FromBody] SetStatusRequest request, CancellationToken cancellationToken)
    {
        // "Locked" is not a stored UserStatus value any more (TR-SEC-12 — see
        // AdministrationService.SetUserLockedAsync) — the wire contract still speaks
        // Active/Locked, translated to a bool at this one boundary.
        bool locked;

        switch (request.Status)
        {
            case nameof(UserStatus.Active):
                locked = false;
                break;
            case "Locked":
                locked = true;
                break;
            default:
                return Problem(
                    title: "Invalid status",
                    detail: $"Status must be 'Active' or 'Locked'. Received: '{request.Status}'.",
                    statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await _administrationService.SetUserLockedAsync(userId, locked, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (AdministrationValidationException)
        {
            return NotFound();
        }
    }

    [HttpPost("{userId:long}/password")]
    [RequireJsonPermission(PermissionCodes.UserUpdate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> ChangePassword(long userId, [FromBody] ChangePasswordHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _administrationService.ChangeUserPasswordAsync(userId, request.NewPassword, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (AdministrationValidationException exception) when (IsUserNotFound(exception, userId))
        {
            return NotFound();
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpDelete("{userId:long}")]
    [RequireJsonPermission(PermissionCodes.UserLock)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Delete(long userId, CancellationToken cancellationToken)
    {
        try
        {
            await _administrationService.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (AdministrationValidationException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Distinguishes "no such user" (404) from every other <see cref="AdministrationValidationException"/>
    /// the same call can throw (a bad field, an unknown ISP) — both of which also happen to say
    /// "does not exist", so this matches the exact message <c>AdministrationService</c> throws for
    /// the missing-user case specifically, not a substring.
    /// </summary>
    private static bool IsUserNotFound(AdministrationValidationException exception, long userId) =>
        exception.Message == $"User {userId} does not exist.";

    private ActionResult ValidationProblemFor(AdministrationValidationException exception)
    {
        // TR-NFR-12: each message keyed by the field it concerns, so the drawer can show it next
        // to that field instead of a single combined banner.
        var errors = exception.FieldErrors.Count > 0
            ? exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())
            : exception.Violations.Count > 0
                ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
                : new Dictionary<string, string[]> { ["request"] = [exception.Message] };

        return BadRequest(new ValidationProblemDetails(errors));
    }

    /// <summary>
    /// "Locked" is not stored on <see cref="Application.Identity.Entities.User.Status"/> any more
    /// (TR-SEC-12) — it is derived from <see cref="UserManager{TUser}.IsLockedOutAsync"/> here, so
    /// the wire contract still returns exactly "Active"/"Locked"/"Deleted" as before, unaffected by
    /// the internal representation change.
    /// </summary>
    private async Task<UserResponse> ToResponseAsync(User user)
    {
        var status = user.Status == UserStatus.Deleted
            ? "Deleted"
            : await _userManager.IsLockedOutAsync(user).ConfigureAwait(false) ? "Locked" : "Active";

        return new UserResponse(user.Id, user.IspId, user.FullName, user.Email!, user.Mobile, user.Role.Name!, status, user.LastLoginAt);
    }
}

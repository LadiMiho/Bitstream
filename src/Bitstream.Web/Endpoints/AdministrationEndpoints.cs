using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Endpoints;

/// <summary>
/// TRD 4.2: ISP and user administration. Creation and lock/unlock require the Administrator
/// role's permissions (TR-SEC-09); the two read endpoints are open to any authenticated caller
/// at the route level, and <see cref="IAdministrationService"/> decides — from identity, before
/// touching the repository — whether the specific record is theirs to see (TR-SEC-18, TR-SEC-19).
/// </summary>
public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var isps = app.MapGroup("/api/v1/isps")
            .WithTags("ISP administration")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        isps.MapPost("/", CreateIspAsync)
            .WithName("CreateIsp")
            .WithSummary("Create an ISP")
            .WithDescription("TR-SEC-09, TR-SEC-15: name, NIPT, contact person, contact email, contact mobile and the CRM Business Partner reference are all required and validated.")
            .Accepts<CreateIspHttpRequest>("application/json")
            .Produces<IspResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(PermissionCodes.IspCreate);

        isps.MapGet("/", SearchIspsAsync)
            .WithName("SearchIsps")
            .WithSummary("Browse or search ISPs")
            .WithDescription(
                "TR-SEC-18/19: an Administrator/Auditor (isp.read.all) searches every ISP; " +
                "anyone else's search is narrowed to their own ISP, the same ownership rule " +
                "GetIsp enforces.")
            .Produces<IspListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        isps.MapGet("/{ispId:long}", GetIspAsync)
            .WithName("GetIsp")
            .WithSummary("Read an ISP")
            .WithDescription(
                "TR-SEC-18: an ISP user may read their own ISP. TR-SEC-19: a request for a " +
                "different ISP returns 404, identically to one that does not exist, and is " +
                "logged as a security event.")
            .Produces<IspResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        isps.MapPatch("/{ispId:long}/status", SetIspStatusAsync)
            .WithName("SetIspStatus")
            .WithSummary("Lock or unlock an ISP")
            .WithDescription(
                "TR-SEC-11, TR-SEC-13: locking cascades to every currently-active user of the " +
                "ISP and revokes their sessions immediately. Unlocking does not reciprocally " +
                "unlock them.")
            .Accepts<SetStatusRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PermissionCodes.IspLock);

        var users = app.MapGroup("/api/v1/users")
            .WithTags("User administration")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        users.MapPost("/", CreateUserAsync)
            .WithName("CreateUser")
            .WithSummary("Create a portal user")
            .WithDescription("TR-SEC-09, TR-SEC-14: full name, RFC-compliant unique email and E.164 mobile are required; the initial password must satisfy the configured policy (TR-SEC-03).")
            .Accepts<CreateUserHttpRequest>("application/json")
            .Produces<UserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(PermissionCodes.UserCreate);

        users.MapGet("/", SearchUsersAsync)
            .WithName("SearchUsers")
            .WithSummary("Browse or search users")
            .WithDescription(
                "An Administrator/Auditor (isp.read.all) searches every user; anyone else's " +
                "search can only ever find themselves — this module has no directory of " +
                "teammates, the same rule GetUser enforces.")
            .Produces<UserListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        users.MapGet("/{userId:long}", GetUserAsync)
            .WithName("GetUser")
            .WithSummary("Read a user")
            .WithDescription("Self, or an Administrator/Auditor holding isp.read.all. Anyone else gets 404 (TR-SEC-19).")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        users.MapPatch("/{userId:long}/status", SetUserStatusAsync)
            .WithName("SetUserStatus")
            .WithSummary("Lock or unlock a user")
            .WithDescription("TR-SEC-11, TR-SEC-12: a locked user is denied authentication and their sessions are revoked immediately (TR-SEC-07).")
            .Accepts<SetStatusRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PermissionCodes.UserLock);

        users.MapPut("/{userId:long}", UpdateUserAsync)
            .WithName("UpdateUser")
            .WithSummary("Edit a user's profile")
            .WithDescription("Full name, email, mobile, role and ISP — the same fields and validation as create, minus the password.")
            .Accepts<UpdateUserHttpRequest>("application/json")
            .Produces<UserResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PermissionCodes.UserUpdate);

        users.MapPost("/{userId:long}/password", ChangeUserPasswordAsync)
            .WithName("ChangeUserPassword")
            .WithSummary("Reset a user's password")
            .WithDescription("TR-SEC-03: validated against the configured policy and password history. Revokes the user's sessions immediately (TR-SEC-07).")
            .Accepts<ChangePasswordHttpRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PermissionCodes.UserUpdate);

        users.MapDelete("/{userId:long}", DeleteUserAsync)
            .WithName("DeleteUser")
            .WithSummary("Delete a user")
            .WithDescription("TR-DAT-07: soft delete only. The user cannot authenticate afterwards and is hidden from search/browse; every audit, session and password-history row is left intact.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PermissionCodes.UserLock);

        return app;
    }

    private static async Task<IResult> CreateIspAsync(
        [FromBody] CreateIspHttpRequest request,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        try
        {
            var isp = await administrationService.CreateIspAsync(
                new CreateIspRequest(request.Name, request.Nipt, request.ContactPerson, request.ContactEmail, request.ContactMobile, request.CrmBpReference),
                cancellationToken).ConfigureAwait(false);

            var response = ToResponse(isp);

            return Results.CreatedAtRoute("GetIsp", new { ispId = isp.IspId }, response);
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    private static async Task<IResult> SearchIspsAsync(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        var result = await administrationService.SearchIspsAsync(
            search, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        return Results.Ok(new IspListResponse([.. result.Items.Select(ToResponse)], result.TotalCount));
    }

    private static async Task<IResult> GetIspAsync(
        [FromRoute] long ispId,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        var isp = await administrationService.GetIspAsync(ispId, cancellationToken).ConfigureAwait(false);

        // Not found and forbidden are the same response on purpose (TR-SEC-19): the service has
        // already decided, from identity alone, whether this ispId is one the caller may see.
        return isp is null ? Results.NotFound() : Results.Ok(ToResponse(isp));
    }

    private static async Task<IResult> SetIspStatusAsync(
        [FromRoute] long ispId,
        [FromBody] SetStatusRequest request,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        if (!TryParseStatus<IspStatus>(request.Status, out var status))
        {
            return Results.Problem(
                title: "Invalid status",
                detail: $"Status must be 'Active' or 'Locked'. Received: '{request.Status}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await administrationService.SetIspStatusAsync(ispId, status, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (AdministrationValidationException)
        {
            // Administrator-only endpoint: reaching this point with an unknown ispId is an
            // ordinary not-found, not a TR-SEC-19 event — the caller was already entitled to
            // look, the target just does not exist.
            return Results.NotFound();
        }
    }

    private static async Task<IResult> CreateUserAsync(
        [FromBody] CreateUserHttpRequest request,
        IAdministrationService administrationService,
        UserManager<User> userManager,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await administrationService.CreateUserAsync(
                new CreateUserRequest(request.IspId, request.FullName, request.Email, request.Mobile, request.RoleName, request.InitialPassword),
                cancellationToken).ConfigureAwait(false);

            var response = await ToResponseAsync(user, userManager).ConfigureAwait(false);

            return Results.CreatedAtRoute("GetUser", new { userId = user.Id }, response);
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    private static async Task<IResult> SearchUsersAsync(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        IAdministrationService administrationService,
        UserManager<User> userManager,
        CancellationToken cancellationToken)
    {
        var result = await administrationService.SearchUsersAsync(
            search, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        var items = await Task.WhenAll(result.Items.Select(user => ToResponseAsync(user, userManager))).ConfigureAwait(false);

        return Results.Ok(new UserListResponse(items, result.TotalCount));
    }

    private static async Task<IResult> GetUserAsync(
        [FromRoute] long userId,
        IAdministrationService administrationService,
        UserManager<User> userManager,
        CancellationToken cancellationToken)
    {
        var user = await administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);

        return user is null ? Results.NotFound() : Results.Ok(await ToResponseAsync(user, userManager).ConfigureAwait(false));
    }

    private static async Task<IResult> SetUserStatusAsync(
        [FromRoute] long userId,
        [FromBody] SetStatusRequest request,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
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
                return Results.Problem(
                    title: "Invalid status",
                    detail: $"Status must be 'Active' or 'Locked'. Received: '{request.Status}'.",
                    statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await administrationService.SetUserLockedAsync(userId, locked, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (AdministrationValidationException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdateUserAsync(
        [FromRoute] long userId,
        [FromBody] UpdateUserHttpRequest request,
        IAdministrationService administrationService,
        UserManager<User> userManager,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await administrationService.UpdateUserAsync(
                userId, new UpdateUserRequest(request.IspId, request.FullName, request.Email, request.Mobile, request.RoleName),
                cancellationToken).ConfigureAwait(false);

            return Results.Ok(await ToResponseAsync(user, userManager).ConfigureAwait(false));
        }
        catch (AdministrationValidationException exception) when (IsUserNotFound(exception, userId))
        {
            return Results.NotFound();
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    private static async Task<IResult> ChangeUserPasswordAsync(
        [FromRoute] long userId,
        [FromBody] ChangePasswordHttpRequest request,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        try
        {
            await administrationService.ChangeUserPasswordAsync(userId, request.NewPassword, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (AdministrationValidationException exception) when (IsUserNotFound(exception, userId))
        {
            return Results.NotFound();
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblem(exception);
        }
    }

    private static async Task<IResult> DeleteUserAsync(
        [FromRoute] long userId,
        IAdministrationService administrationService,
        CancellationToken cancellationToken)
    {
        try
        {
            await administrationService.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (AdministrationValidationException)
        {
            return Results.NotFound();
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

    private static IResult ValidationProblem(AdministrationValidationException exception) =>
        Results.ValidationProblem(
            exception.FieldErrors.Count > 0
                // TR-NFR-12: each message keyed by the field it concerns, so the drawer can show
                // it next to that field instead of a single combined banner.
                ? exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())
                : exception.Violations.Count > 0
                    ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
                    : new Dictionary<string, string[]> { ["request"] = [exception.Message] });

    private static bool TryParseStatus<TStatus>(string value, out TStatus status)
        where TStatus : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out status);

    private static IspResponse ToResponse(Isp isp) =>
        new(isp.IspId, isp.Name, isp.Nipt, isp.ContactPerson, isp.ContactEmail, isp.ContactMobile,
            isp.CrmBpReference, isp.Status.ToString(), isp.CreatedAt);

    /// <summary>
    /// "Locked" is not stored on <see cref="User.Status"/> any more (TR-SEC-12) — it is derived
    /// from <c>UserManager.IsLockedOutAsync</c> here, so the wire contract still returns exactly
    /// "Active"/"Locked"/"Deleted" as before, unaffected by the internal representation change.
    /// </summary>
    private static async Task<UserResponse> ToResponseAsync(User user, UserManager<User> userManager)
    {
        var status = user.Status == UserStatus.Deleted
            ? "Deleted"
            : await userManager.IsLockedOutAsync(user).ConfigureAwait(false) ? "Locked" : "Active";

        return new UserResponse(user.Id, user.IspId, user.FullName, user.Email!, user.Mobile, user.Role.Name!, status, user.LastLoginAt);
    }
}

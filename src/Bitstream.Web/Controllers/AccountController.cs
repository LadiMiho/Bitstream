using Bitstream.Application.Configuration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Bitstream.Web.Controllers;

/// <summary>What the My account and Change password drawers show: the signed-in user's own details and the password rules.</summary>
public sealed record AccountViewModel(
    string FullName,
    string Email,
    string Mobile,
    string RoleName,
    string? IspName,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? PasswordUpdatedAt,
    int PasswordMinLength,
    int PasswordMinCharacterClasses);

/// <summary>
/// The user menu's two drawers, for every signed-in user, ISP users included: My account (their
/// own details) and Change password (current password required). The current session stays
/// signed in after the change; any other session of the user ends. Script:
/// <c>wwwroot/js/account-menu.js</c>.
/// </summary>
[Route("Account")]
public sealed class AccountController : Controller
{
    private readonly IAdministrationService _administrationService;
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;
    private readonly IOptionsMonitor<PasswordPolicyOptions> _passwordPolicy;

    public AccountController(
        IAdministrationService administrationService,
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IOptionsMonitor<PasswordPolicyOptions> passwordPolicy)
    {
        _administrationService = administrationService;
        _userManager = userManager;
        _signInManager = signInManager;
        _passwordPolicy = passwordPolicy;
    }

    /// <summary>My account drawer: the signed-in user's own details.</summary>
    [HttpGet("ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(CancellationToken cancellationToken) =>
        await ModelAsync(cancellationToken).ConfigureAwait(false) is { } model ? PartialView("_AccountDrawer", model) : NotFound();

    /// <summary>Change password drawer.</summary>
    [HttpGet("PasswordDrawer")]
    [RequireSession]
    public async Task<IActionResult> PasswordDrawer(CancellationToken cancellationToken) =>
        await ModelAsync(cancellationToken).ConfigureAwait(false) is { } model ? PartialView("_PasswordDrawer", model) : NotFound();

    [HttpPost("Password")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangeOwnPasswordHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["confirmPassword"] = ["The two new passwords are not the same."]
            }));
        }

        try
        {
            await _administrationService.ChangeOwnPasswordAsync(request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AdministrationValidationException exception)
        {
            var errors = exception.FieldErrors.Count > 0
                ? exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())
                : new Dictionary<string, string[]> { ["request"] = exception.Violations.Count > 0 ? [.. exception.Violations] : [exception.Message] };

            return BadRequest(new ValidationProblemDetails(errors));
        }

        // The change rotated the security stamp, which would end this session too on its next
        // request; re-issue its cookie so only the user's other sessions end.
        if (await _userManager.GetUserAsync(User).ConfigureAwait(false) is { } user)
        {
            await _signInManager.RefreshSignInAsync(user).ConfigureAwait(false);
        }

        return NoContent();
    }

    private async Task<AccountViewModel?> ModelAsync(CancellationToken cancellationToken)
    {
        if (!long.TryParse(_userManager.GetUserId(User), out var userId)
            || await _administrationService.GetUserAsync(userId, cancellationToken).ConfigureAwait(false) is not { } user)
        {
            return null;
        }

        var isp = user.IspId is { } ispId ? await _administrationService.GetIspAsync(ispId, cancellationToken).ConfigureAwait(false) : null;
        var policy = _passwordPolicy.CurrentValue;

        return new AccountViewModel(
            user.FullName, user.Email ?? string.Empty, user.Mobile, user.Role.Name ?? string.Empty, isp?.Name,
            user.TwoFactorEnabled, user.CreatedAt, user.LastLoginAt, user.PasswordUpdatedAt,
            policy.MinLength, policy.MinCharacterClasses);
    }
}

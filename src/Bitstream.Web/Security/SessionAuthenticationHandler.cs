using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Bitstream.Application.Services;
using Bitstream.Hosting.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
// Sdk.Web's implicit global usings bring in Microsoft.AspNetCore.Builder, which also declares a
// SessionOptions (the session-state-middleware one) — disambiguate in favour of ours.
using SessionOptions = Bitstream.Application.Configuration.SessionOptions;

namespace Bitstream.Web.Security;

/// <summary>
/// Authenticates a request from its session cookie (TR-SEC-07), by asking
/// <see cref="IIdentityService.ValidateSessionAsync"/> — never by trusting anything encoded in
/// the cookie itself.
/// <para>
/// The cookie holds only an opaque, random token: no claims, no signature the server has to
/// verify offline, nothing a stolen cookie's holder could learn from. Every request costs one
/// database look-up, and that is deliberate — it is what makes "invalidate a session at logout
/// or at lock" (TR-SEC-07) actually true rather than aspirational. A signed, self-contained
/// token (a JWT, say) cannot be revoked before it expires without a second, separate revocation
/// list — at which point it is this mechanism anyway, with extra steps.
/// </para>
/// <para>
/// Claims are rebuilt from the database on every request, including the permission set
/// (TR-SEC-17): a role's permissions changing takes effect on the caller's very next request,
/// not only after they log in again.
/// </para>
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>Name this scheme is registered under.</summary>
    public const string SchemeName = "BitstreamSession";

    private readonly IIdentityService _identityService;
    private readonly IOptionsMonitor<SessionOptions> _sessionOptions;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IIdentityService identityService,
        IOptionsMonitor<SessionOptions> sessionOptions)
        : base(options, logger, encoder)
    {
        _identityService = identityService;
        _sessionOptions = sessionOptions;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cookieName = _sessionOptions.CurrentValue.CookieName;

        if (!Request.Cookies.TryGetValue(cookieName, out var token) || string.IsNullOrWhiteSpace(token))
        {
            // No credential presented at all — distinct from a bad one. ASP.NET Core maps this
            // to "proceed as anonymous," which is correct for a public endpoint but leaves a
            // protected one to reject the request at authorisation with 401, not here.
            return AuthenticateResult.NoResult();
        }

        var user = await _identityService.ValidateSessionAsync(token, Context.RequestAborted).ConfigureAwait(false);

        if (user is null)
        {
            return AuthenticateResult.Fail("The session is invalid, expired or revoked.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.RoleName)
        };

        if (user.IspId is { } ispId)
        {
            claims.Add(new Claim(BitstreamClaimTypes.IspId, ispId.ToString(CultureInfo.InvariantCulture)));
        }

        claims.AddRange(user.Permissions.Select(permission => new Claim(BitstreamClaimTypes.Permission, permission)));

        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}

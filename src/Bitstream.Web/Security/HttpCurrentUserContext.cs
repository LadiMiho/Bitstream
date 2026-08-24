using System.Globalization;
using System.Security.Claims;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Hosting.Middleware;
using Bitstream.Hosting.Security;

namespace Bitstream.Web.Security;

/// <summary>
/// Implements <see cref="ICurrentUserContext"/> by reading the claims
/// <see cref="BitstreamClaimsPrincipalFactory"/> put on the request's <see cref="ClaimsPrincipal"/>
/// (via ASP.NET Core Identity's own cookie authentication — <c>Program.cs</c>). The one place in
/// the solution that turns "who is making this HTTP request" into the ambient identity the
/// application layer authorises against.
/// <para>
/// Scoped, not singleton: it wraps <see cref="IHttpContextAccessor"/>, so a new instance is
/// resolved for whichever request is current in this scope, the same lifetime as
/// <c>BitstreamDbContext</c>.
/// </para>
/// </summary>
public sealed class HttpCurrentUserContext : ICurrentUserContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCurrentUserContext(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public long? UserId => ParseLong(User?.FindFirstValue(ClaimTypes.NameIdentifier));

    public long? IspId => ParseLong(User?.FindFirstValue(BitstreamClaimTypes.IspId));

    public string? RoleName => User?.FindFirstValue(ClaimTypes.Role);

    // Populated regardless of authentication state, so an anonymous failed login is still
    // attributable to an IP address in the audit log (TR-SEC-22).
    public string? ActorIp => _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string CorrelationId =>
        _httpContextAccessor.HttpContext?.Items[CorrelationIdMiddleware.HeaderName] as string ??
        // No request in scope — this type is HTTP-specific and nothing in this module runs it
        // from a background job today, but failing loudly here would take down an otherwise
        // unrelated audit write, so a fresh, unlinked ID is generated instead.
        Guid.NewGuid().ToString("n");

    public bool HasPermission(string permissionCode) =>
        !string.IsNullOrEmpty(permissionCode) && (User?.HasClaim(BitstreamClaimTypes.Permission, permissionCode) ?? false);

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : null;
}

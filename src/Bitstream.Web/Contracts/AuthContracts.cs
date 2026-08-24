using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

/// <param name="Email">TR-SEC-01.</param>
/// <param name="Password">Checked against the stored Argon2 hash (TR-SEC-02).</param>
public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password);

/// <param name="Channel">Where the second factor is coming from — <c>Totp</c>, <c>EmailOtp</c> or <c>SmsOtp</c> (TR-SEC-05).</param>
/// <param name="QrCodeDataUri">
/// A <c>data:image/png;base64,...</c> image, present only on this user's first login with the
/// <c>Totp</c> channel — no authenticator key exists yet (<c>UserManager.GetAuthenticatorKeyAsync</c>
/// returns null), so the login page shows this instead of a bare code prompt. Absent on every
/// login after the first successful code. There is no challenge token any more: ASP.NET Core
/// Identity's <c>SignInManager</c> tracks which account is mid-two-factor via its own short-lived
/// cookie, set automatically alongside this response — <c>POST /login/verify</c> just needs the code.
/// </param>
public sealed record TwoFactorRequiredResponse(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("qrCodeDataUri")] string? QrCodeDataUri);

public sealed record TwoFactorVerifyRequest(
    [property: JsonPropertyName("code")] string Code);

/// <summary>Returned after a successful second factor. The session itself travels as an HttpOnly cookie, not in this body.</summary>
public sealed record SessionResponse(
    [property: JsonPropertyName("user")] CurrentUserResponse User,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <param name="UserId">The authenticated caller's ID.</param>
/// <param name="FullName">The authenticated caller's full name.</param>
/// <param name="Email">The authenticated caller's email.</param>
/// <param name="Role">The authenticated caller's role name.</param>
/// <param name="IspId">The authenticated caller's own ISP, or null for an internal user.</param>
/// <param name="Permissions">Codes the caller's role grants (TR-SEC-17) — informs which controls the frontend renders; every one is still checked server-side (TR-SEC-20).</param>
public sealed record CurrentUserResponse(
    [property: JsonPropertyName("userId")] long UserId,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("ispId")] long? IspId,
    [property: JsonPropertyName("permissions")] IReadOnlyList<string> Permissions);

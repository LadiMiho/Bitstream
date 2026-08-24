using Bitstream.Domain.Enums;

namespace Bitstream.Application.Configuration;

/// <summary>
/// Argon2id cost parameters (TR-SEC-02).
/// <para>
/// Defaults follow the OWASP Password Storage Cheat Sheet's Argon2id baseline (memory 19 MiB,
/// 2 iterations, 1 degree of parallelism) — the minimum OWASP still calls adequate, not a
/// generous margin above it. Configurable so an environment with headroom can raise it without
/// a release (TR-ARC-06); the validator refuses to go below the OWASP floor, because a
/// configuration change is exactly the kind of accidental weakening TR-SEC-02 exists to prevent.
/// </para>
/// </summary>
public sealed class Argon2Options
{
    /// <summary>Memory in KiB. OWASP floor: 19456 (19 MiB).</summary>
    public int MemorySizeKb { get; set; } = 19456;

    /// <summary>OWASP floor: 2.</summary>
    public int Iterations { get; set; } = 2;

    /// <summary>OWASP floor: 1.</summary>
    public int Parallelism { get; set; } = 1;
}

/// <summary>
/// Password policy, TR-SEC-03: minimum 12 characters, at least three character classes,
/// rejection against a common-password list, no reuse of the last 5 passwords.
/// </summary>
public sealed class PasswordPolicyOptions
{
    public const string SectionName = "Security:PasswordPolicy";

    /// <summary>TR-SEC-03 floor: 12.</summary>
    public int MinLength { get; set; } = 12;

    /// <summary>Counted from: lowercase, uppercase, digit, symbol. TR-SEC-03 floor: 3.</summary>
    public int MinCharacterClasses { get; set; } = 3;

    /// <summary>TR-SEC-03 floor: 5.</summary>
    public int PasswordHistoryCount { get; set; } = 5;

    /// <summary>
    /// Denied passwords beyond the built-in baseline list (<see cref="Services.Identity.CommonPasswordList.Default"/>).
    /// The baseline is a curated top list, not an exhaustive breach corpus — extend it here with
    /// an organisation-specific blocklist before go-live if a larger corpus is required.
    /// </summary>
    public IList<string> AdditionalDeniedPasswords { get; set; } = [];

    public Argon2Options Argon2 { get; set; } = new();
}

/// <summary>
/// Two-factor authentication, TR-SEC-04 / TR-SEC-05. Enforced for every user at every login via
/// ASP.NET Core Identity's own token providers (<c>UserManager.SetTwoFactorEnabledAsync</c> /
/// <c>SignInManager.TwoFactorAuthenticatorSignInAsync</c> / <c>TwoFactorSignInAsync</c>) — code
/// length, validity window and per-code attempt handling are Identity's own, no longer
/// independently configurable here. Only which channel is active stays a per-environment choice
/// (the production choice is TRD 11.4 open item 13).
/// </summary>
public sealed class TwoFactorOptions
{
    public const string SectionName = "Security:TwoFactor";

    /// <summary>Configured second-factor channel. Default Totp: it needs no delivery channel, so it works before open item 13 is answered.</summary>
    public TwoFactorChannel Channel { get; set; } = TwoFactorChannel.Totp;
}

/// <summary>
/// Session lifetime, TR-SEC-07, applied to ASP.NET Core Identity's own authentication cookie
/// (<c>Bitstream.Web/Program.cs</c>'s <c>ConfigureApplicationCookie</c>) — <see cref="IdleTimeout"/>
/// as the cookie's sliding expiry, <see cref="AbsoluteTimeout"/> as an extra cap enforced from a
/// custom <c>OnValidatePrincipal</c> check, since cookie auth alone only offers one or the other.
/// </summary>
public sealed class SessionOptions
{
    public const string SectionName = "Security:Session";

    /// <summary>TR-SEC-07: 30 minutes.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>TR-SEC-07: 12 hours, whichever of the two is reached first.</summary>
    public TimeSpan AbsoluteTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Name of the authentication cookie.</summary>
    public string CookieName { get; set; } = "bitstream_session";
}

/// <summary>Account lockout, TR-SEC-06.</summary>
public sealed class LockoutOptions
{
    public const string SectionName = "Security:Lockout";

    /// <summary>TR-SEC-06: 5 consecutive failed attempts.</summary>
    public int MaxFailedAttempts { get; set; } = 5;
}

namespace Bitstream.Hosting.Configuration;

/// <summary>
/// Named rate-limiting policies, referenced by the endpoint groups in both hosts and
/// configured from <see cref="RateLimitOptions"/> (TR-SEC-29, TR-INT-30).
/// <para>
/// Shared rather than declared per host because the names are the contract between the
/// registration in a host's <c>Program</c> and the <c>RequireRateLimiting</c> call on an
/// endpoint group: a typo on either side silently leaves an endpoint unlimited, and a
/// constant that both sides compile against cannot drift. Each host registers only the
/// policies its own endpoints use.
/// </para>
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>The CRM inbound event API — API host (TR-INT-30).</summary>
    public const string CrmInbound = "crm-inbound";

    /// <summary>Administration and creation endpoints — portal host.</summary>
    public const string Administration = "administration";

    /// <summary>Sign-in, tighter than <see cref="Administration"/>: this is where credential stuffing lands (TR-SEC-29).</summary>
    public const string Authentication = "authentication";
}

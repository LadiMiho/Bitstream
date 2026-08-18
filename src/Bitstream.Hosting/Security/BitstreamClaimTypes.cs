namespace Bitstream.Hosting.Security;

/// <summary>
/// Claim types the session authentication handler puts on the principal, beyond the standard
/// <see cref="System.Security.Claims.ClaimTypes"/> ones it also sets (NameIdentifier, Name,
/// Email, Role).
/// </summary>
public static class BitstreamClaimTypes
{
    /// <summary>The caller's own ISP, present only for ISP users (TR-SEC-18).</summary>
    public const string IspId = "bitstream:isp_id";

    /// <summary>
    /// One claim per permission code the caller's role grants (TR-SEC-17). Set fresh on every
    /// request from the database, not cached in the session record, so a permission change
    /// takes effect on the caller's very next request.
    /// </summary>
    public const string Permission = "bitstream:permission";
}

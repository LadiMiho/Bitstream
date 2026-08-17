using System.Security.Cryptography;
using System.Text;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Hashes opaque bearer tokens (session cookies, 2FA codes) before they are looked up or
/// stored, so that a copy of the database does not hand out anything usable — the same
/// discipline TR-SEC-02 applies to passwords, applied here to session and code storage.
/// <para>
/// Public so that the presentation layer's authentication handler can hash an inbound cookie
/// value with exactly the algorithm the session was stored under, without duplicating it.
/// </para>
/// </summary>
public static class TokenHashing
{
    /// <summary>SHA-256 of the UTF-8 bytes of <paramref name="value"/>, lower-case hex.</summary>
    public static string Sha256Hex(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        return Convert.ToHexStringLower(hash);
    }

    /// <summary>A fresh 256-bit random token, base64url-encoded (no padding) for use as a cookie value.</summary>
    public static string GenerateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>A random numeric one-time code of <paramref name="digits"/> digits, avoiding modulo bias.</summary>
    public static string GenerateNumericCode(int digits)
    {
        if (digits is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, "Must be between 1 and 9.");
        }

        var max = (int)Math.Pow(10, digits);
        var ceiling = (int.MaxValue / max) * max;
        int value;

        do
        {
            value = RandomNumberGenerator.GetInt32(int.MaxValue);
        } while (value >= ceiling);

        return (value % max).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}

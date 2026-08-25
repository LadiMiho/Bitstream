using System.Security.Cryptography;
using System.Text;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Computes the current RFC 6238 TOTP code for a base32 authenticator key — standing in for a
/// physical authenticator app in tests. <c>UserManager.GenerateTwoFactorTokenAsync</c> cannot be
/// used for this: ASP.NET Core Identity's own <c>AuthenticatorTokenProvider&lt;TUser&gt;.GenerateAsync</c>
/// deliberately returns an empty string, since in a real login nothing server-side ever generates
/// this code — it comes from the user's own device. <c>ValidateAsync</c> (the half Identity does
/// implement) checks a code against exactly this algorithm — 30-second steps, HMAC-SHA1, 6 digits,
/// matching Google Authenticator/RFC 6238 — so recomputing it here validates against the real
/// server-side check, not a stand-in for it.
/// </summary>
internal static class TotpCodeGenerator
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    public static string GenerateCode(string base32Key)
    {
        var key = FromBase32(base32Key);
        var timestep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
        var timestepBytes = BitConverter.GetBytes(timestep);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(timestepBytes);
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timestepBytes);

        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var code = binaryCode % (int)Math.Pow(10, Digits);

        return code.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    private static byte[] FromBase32(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        var trimmed = base32.TrimEnd('=').ToUpperInvariant();
        var bits = new StringBuilder(trimmed.Length * 5);

        foreach (var c in trimmed)
        {
            var index = alphabet.IndexOf(c);

            if (index < 0)
            {
                throw new FormatException($"'{c}' is not a valid base32 character.");
            }

            bits.Append(Convert.ToString(index, 2).PadLeft(5, '0'));
        }

        var byteCount = bits.Length / 8;
        var bytes = new byte[byteCount];

        for (var i = 0; i < byteCount; i++)
        {
            bytes[i] = Convert.ToByte(bits.ToString(i * 8, 8), 2);
        }

        return bytes;
    }
}

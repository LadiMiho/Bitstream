using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// RFC 6238 time-based one-time codes over HMAC-SHA1 (RFC 4226), for the <c>Totp</c> channel
/// (TR-SEC-04). Pure BCL cryptography — no third-party TOTP package needed.
/// </summary>
public sealed class TotpService : ITotpService
{
    private const int SecretSizeBytes = 20;

    private readonly IOptionsMonitor<TwoFactorOptions> _options;

    public TotpService(IOptionsMonitor<TwoFactorOptions> options) => _options = options;

    public byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretSizeBytes);

    public string GenerateCurrentCode(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var options = _options.CurrentValue;

        return ComputeCode(secret, CurrentCounter(options.TotpStepSeconds), options.CodeLength);
    }

    public bool Validate(byte[] secret, string code)
    {
        ArgumentNullException.ThrowIfNull(secret);

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var options = _options.CurrentValue;
        var currentCounter = CurrentCounter(options.TotpStepSeconds);
        var codeBytes = Encoding.ASCII.GetBytes(code.Trim());

        // Tried across the tolerated skew window so a slow submission or a clock a few seconds
        // off does not fail a code the app genuinely displayed.
        for (var skew = -options.TotpAllowedSkewSteps; skew <= options.TotpAllowedSkewSteps; skew++)
        {
            var candidate = ComputeCode(secret, currentCounter + skew, options.CodeLength);
            var candidateBytes = Encoding.ASCII.GetBytes(candidate);

            if (candidateBytes.Length == codeBytes.Length &&
                CryptographicOperations.FixedTimeEquals(candidateBytes, codeBytes))
            {
                return true;
            }
        }

        return false;
    }

    public string BuildProvisioningUri(byte[] secret, string accountLabel, string issuer)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLabel);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);

        var options = _options.CurrentValue;
        var encodedIssuer = Uri.EscapeDataString(issuer);
        var encodedLabel = Uri.EscapeDataString(accountLabel);
        var base32Secret = Base32.Encode(secret);

        return string.Create(CultureInfo.InvariantCulture,
            $"otpauth://totp/{encodedIssuer}:{encodedLabel}?secret={base32Secret}&issuer={encodedIssuer}" +
            $"&algorithm=SHA1&digits={options.CodeLength}&period={options.TotpStepSeconds}");
    }

    private static long CurrentCounter(int stepSeconds) =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds() / stepSeconds;

    private static string ComputeCode(byte[] secret, long counter, int digits)
    {
        var counterBytes = BitConverter.GetBytes(counter);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counterBytes);
        }

        var hash = HMACSHA1.HashData(secret, counterBytes);

        // RFC 4226 dynamic truncation.
        var offset = hash[^1] & 0x0F;
        var binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var truncated = binaryCode % (int)Math.Pow(10, digits);

        return truncated.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>RFC 4648 base32, no padding — the encoding authenticator apps expect in a TOTP secret URI.</summary>
    private static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder((data.Length * 8 + 4) / 5);
            var buffer = 0;
            var bitsInBuffer = 0;

            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bitsInBuffer += 8;

                while (bitsInBuffer >= 5)
                {
                    bitsInBuffer -= 5;
                    builder.Append(Alphabet[(buffer >> bitsInBuffer) & 0x1F]);
                }
            }

            if (bitsInBuffer > 0)
            {
                builder.Append(Alphabet[(buffer << (5 - bitsInBuffer)) & 0x1F]);
            }

            return builder.ToString();
        }
    }
}

using System.Globalization;
using System.Security.Cryptography;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Configuration;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Argon2id password hashing (TR-SEC-02), one of the three algorithms the requirement names.
/// <para>
/// Stores the salt and cost parameters inside the returned string in PHC format
/// (<c>$argon2id$v=19$m=...,t=...,p=...$&lt;salt&gt;$&lt;hash&gt;</c>) so that
/// <see cref="Verify"/> and <see cref="NeedsRehash"/> work correctly across a change to the
/// configured cost: an old hash still verifies against the parameters it was created with, and
/// is flagged for opportunistic rehashing under the new ones on the next successful login.
/// </para>
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    private readonly IOptionsMonitor<PasswordPolicyOptions> _options;

    public Argon2PasswordHasher(IOptionsMonitor<PasswordPolicyOptions> options) => _options = options;

    public string AlgorithmTag => "Argon2id";

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var argon2 = _options.CurrentValue.Argon2;
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = ComputeHash(password, salt, argon2.MemorySizeKb, argon2.Iterations, argon2.Parallelism);

        return Format(argon2.MemorySizeKb, argon2.Iterations, argon2.Parallelism, salt, hash);
    }

    public bool Verify(string password, string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentException.ThrowIfNullOrEmpty(hash);

        if (!TryParse(hash, out var memoryKb, out var iterations, out var parallelism, out var salt, out var expected))
        {
            return false;
        }

        var actual = ComputeHash(password, salt, memoryKb, iterations, parallelism);

        // Fixed-time comparison: TR-SEC-02 asks for a one-way hash, and a length/short-circuit
        // comparison here would leak timing information about how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public bool NeedsRehash(string hash)
    {
        ArgumentException.ThrowIfNullOrEmpty(hash);

        if (!TryParse(hash, out var memoryKb, out var iterations, out var parallelism, out _, out _))
        {
            // Unparseable means it is not one of ours (or is corrupt); treat as needing rehash
            // so a successful verify elsewhere still results in a fresh hash being stored.
            return true;
        }

        var current = _options.CurrentValue.Argon2;

        return memoryKb < current.MemorySizeKb || iterations < current.Iterations || parallelism < current.Parallelism;
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(System.Text.Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };

        return argon2.GetBytes(HashSizeBytes);
    }

    private static string Format(int memoryKb, int iterations, int parallelism, byte[] salt, byte[] hash) =>
        string.Create(CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={memoryKb},t={iterations},p={parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");

    private static bool TryParse(
        string encoded,
        out int memoryKb,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] hash)
    {
        memoryKb = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        // $argon2id$v=19$m=...,t=...,p=...$<salt>$<hash>
        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 5 || parts[0] != "argon2id" || !parts[1].StartsWith("v=", StringComparison.Ordinal))
        {
            return false;
        }

        var costParts = parts[2].Split(',');

        if (costParts.Length != 3)
        {
            return false;
        }

        if (!TryParseCostComponent(costParts[0], "m=", out memoryKb) ||
            !TryParseCostComponent(costParts[1], "t=", out iterations) ||
            !TryParseCostComponent(costParts[2], "p=", out parallelism))
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[3]);
            hash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        return true;
    }

    private static bool TryParseCostComponent(string component, string prefix, out int value)
    {
        value = 0;

        return component.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(component.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

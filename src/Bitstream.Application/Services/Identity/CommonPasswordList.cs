namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Baseline common-password blocklist for TR-SEC-03.
/// <para>
/// This is a curated list of well-known weak passwords and predictable patterns (keyboard
/// walks, "password" variants, common words followed by a digit or year), not an exhaustive
/// breach corpus — a full 10-million-entry list is impractical to ship inline and mostly
/// redundant once the length and character-class rules already apply. It exists to catch the
/// specific, still-common failure mode those rules do not: a password that is long and varied
/// enough to pass them by construction (<c>Password123!</c>) while still being the first thing
/// an attacker tries.
/// </para>
/// <para>
/// Extend it via <c>Security:PasswordPolicy:AdditionalDeniedPasswords</c> (TR-ARC-06) rather
/// than editing this file — an organisation-specific blocklist, or a larger corpus, belongs in
/// configuration so it can change without a release.
/// </para>
/// </summary>
public static class CommonPasswordList
{
    /// <summary>Comparison used throughout: common-password checks are case-insensitive.</summary>
    public static readonly IEqualityComparer<string> Comparer = StringComparer.OrdinalIgnoreCase;

    public static readonly IReadOnlySet<string> Default = new HashSet<string>(Comparer)
    {
        // Classics
        "password", "passw0rd", "p@ssword", "p@ssw0rd", "password1", "password123",
        "letmein", "letmein123", "trustno1", "iloveyou", "sunshine", "princess",
        "admin", "administrator", "welcome", "welcome1", "monkey", "dragon",
        "master", "shadow", "superman", "batman", "starwars", "football",
        "baseball", "basketball", "soccer", "hockey", "golfer",

        // Keyboard walks
        "qwerty", "qwerty123", "qwertyuiop", "asdfgh", "asdfghjkl", "zxcvbn",
        "zxcvbnm", "1qaz2wsx", "qazwsx", "1q2w3e4r", "1q2w3e", "q1w2e3r4",

        // Numeric sequences
        "123456", "1234567", "12345678", "123456789", "1234567890",
        "654321", "987654321", "111111", "121212", "112233", "000000",

        // Company / role patterns — the ones an ISP portal user is most likely to try
        "changeme", "changeit", "temp1234", "temppass", "newpassword",
        "letmein1", "guest1234", "guestuser", "user1234", "test1234",
        "bitstream", "bitstream1", "bitstream123", "wholesale1", "isp12345",

        // "Word + number" pattern, the single most common structure
        "summer2024", "summer2025", "winter2024", "winter2025",
        "spring2024", "spring2025", "autumn2024", "autumn2025",
        "abc123", "abc12345", "a1b2c3d4", "qwe123", "test123",

        // Phrases
        "iloveyou1", "letmeinplease", "opensesame", "whateveryousaid", "ihateyou",
        "ilovetoyou", "trustno1234", "adminadmin", "rootroot", "toor",
    };
}

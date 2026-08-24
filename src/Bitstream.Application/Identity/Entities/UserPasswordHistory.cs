namespace Bitstream.Application.Identity.Entities;

/// <summary>
/// Previous password hashes for the "no reuse of the last 5 passwords" rule (TR-SEC-03).
/// Not an entity of TRD 3.1; added because the rule cannot be satisfied without it. Moved out of
/// <c>Bitstream.Domain</c> alongside <see cref="User"/> purely because it navigates to it — the
/// table itself (<c>sec.UserPasswordHistory</c>) stays hand-written, unmigrated.
/// </summary>
public sealed class UserPasswordHistory
{
    public long PasswordHistoryId { get; set; }

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    public required string PasswordHash { get; set; }

    public required string PasswordHashAlgorithm { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

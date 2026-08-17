using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Portal user. TRD 3.1 "User".
/// Email is unique across the platform (TR-SEC-01); internal users (Wholesale,
/// Service Desk, Auditor) carry no <see cref="IspId"/>.
/// </summary>
public sealed class User
{
    public long UserId { get; set; }

    /// <summary>Owning ISP, or null for internal users.</summary>
    public long? IspId { get; set; }

    public Isp? Isp { get; set; }

    public required string FullName { get; set; }

    /// <summary>RFC-compliant, unique across the platform (TR-SEC-14).</summary>
    public required string Email { get; set; }

    /// <summary>E.164 format (TR-SEC-14).</summary>
    public required string Mobile { get; set; }

    public long RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Reset on success; account locks at 5 (TR-SEC-06).</summary>
    public int FailedLoginCount { get; set; }

    // --- Credential material. Not listed in TRD 3.1 but mandated by TR-SEC-02/03. ---

    /// <summary>Salted adaptive hash (Argon2id/bcrypt/scrypt). Reversible storage is prohibited (TR-SEC-02).</summary>
    public required string PasswordHash { get; set; }

    /// <summary>Algorithm and cost parameters, so that hashes can be upgraded in place.</summary>
    public required string PasswordHashAlgorithm { get; set; }

    public DateTimeOffset? PasswordUpdatedAt { get; set; }

    /// <summary>Encrypted TOTP seed, when the configured 2FA channel is TOTP (TR-SEC-04, open item 13).</summary>
    public byte[]? TotpSecret { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? CreatedBy { get; set; }

    /// <summary>Last 5 hashes, for the no-reuse rule (TR-SEC-03).</summary>
    public ICollection<UserPasswordHistory> PasswordHistory { get; set; } = [];
}

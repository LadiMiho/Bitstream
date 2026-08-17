namespace Bitstream.Domain.Entities;

/// <summary>
/// A signed-in session, TR-SEC-07.
/// <para>
/// Not a TRD 3.1 entity — the TRD does not enumerate a session store, but "sessions must
/// expire... session tokens must be invalidated at logout and at lock" cannot be satisfied by
/// a stateless token: only a server-side record can be revoked on demand. This table is that
/// record. Expiry is enforced from two independent points: <see cref="ExpiresAt"/> is the fixed
/// 12-hour absolute cap set at issuance, and <see cref="LastActivityAt"/> is compared against
/// the configured idle timeout (default 30 minutes) on every authenticated request — whichever
/// is reached first ends the session.
/// </para>
/// <para>
/// <see cref="TokenHash"/> holds a SHA-256 hash of the opaque token issued to the browser as an
/// HttpOnly cookie, never the token itself: a copy of this table does not hand out usable
/// sessions, the same discipline TR-SEC-02 applies to passwords.
/// </para>
/// </summary>
public sealed class UserSession
{
    public long SessionId { get; set; }

    public long UserId { get; set; }

    public User User { get; set; } = null!;

    /// <summary>SHA-256 hash of the session token, hex-encoded. The lookup key; unique.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>Absolute expiry: <see cref="IssuedAt"/> plus the configured absolute timeout (TR-SEC-07).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Updated on every authenticated request; the idle-timeout clock (TR-SEC-07).</summary>
    public DateTimeOffset LastActivityAt { get; set; }

    /// <summary>Client IP at issuance, for the audit trail. Not itself a validity check.</summary>
    public string? IssuedFromIp { get; set; }

    /// <summary>Set on logout or on lock; a revoked session is never valid regardless of its timestamps.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Why the session was revoked, e.g. <c>UserSignedOut</c>, <c>AccountLocked</c>, <c>IspLocked</c>.</summary>
    public string? RevokedReason { get; set; }
}

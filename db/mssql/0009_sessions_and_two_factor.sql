/*
    0009_sessions_and_two_factor.sql
    Not TRD 3.1 entities. Added because TR-SEC-07 ("session tokens must be invalidated at
    logout and at lock") and TR-SEC-04 ("the second factor... valid for a maximum of 5 minutes
    and usable once") both need server-side state that survives across requests and, on IIS,
    potentially across worker processes — an in-memory session or challenge would not.

    Neither table carries a no-delete trigger the way sec.Isp and sec.[User] do. TR-DAT-07 binds
    business and identity data; a session or a 2FA challenge is neither — it is a short-lived
    security artefact that is meaningless once expired or revoked. The application account is
    still denied DELETE at the schema level (0008_permissions.sql covers all of sec), so nothing
    in the running application can remove a row from these tables either; a future retention job
    (TRD 11.4 open item 10) is the intended place to prune them, and that job would need its own,
    narrowly-scoped DELETE grant when it is built.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- --------------------------------------------------------------------------------------
-- sec.UserSession — TR-SEC-07
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.UserSession', 'U') IS NULL
BEGIN
    CREATE TABLE sec.UserSession
    (
        SessionId      bigint            IDENTITY(1,1) NOT NULL,
        UserId         bigint            NOT NULL,
        -- SHA-256 hash of the opaque session token, hex-encoded. The lookup key; a copy of this
        -- table does not hand out a usable session, the same discipline TR-SEC-02 applies to
        -- passwords.
        TokenHash      char(64)          NOT NULL,
        IssuedAt       datetimeoffset(7) NOT NULL,
        -- Absolute cap: IssuedAt plus the configured absolute timeout (default 12 hours).
        ExpiresAt      datetimeoffset(7) NOT NULL,
        -- Updated on every authenticated request; compared against the configured idle timeout
        -- (default 30 minutes). TR-SEC-07 expires the session at whichever limit is reached first.
        LastActivityAt datetimeoffset(7) NOT NULL,
        IssuedFromIp   nvarchar(64)      NULL,
        RevokedAt      datetimeoffset(7) NULL,
        RevokedReason  nvarchar(50)      NULL,
        CONSTRAINT PK_UserSession PRIMARY KEY CLUSTERED (SessionId),
        CONSTRAINT UX_UserSession_TokenHash UNIQUE (TokenHash),
        CONSTRAINT FK_UserSession_User FOREIGN KEY (UserId) REFERENCES sec.[User] (UserId),
        CONSTRAINT CK_UserSession_ExpiresAfterIssued CHECK (ExpiresAt > IssuedAt),
        -- A revoked session must say why (UserSignedOut, AccountLocked, IspLocked, IdleTimeout),
        -- and a live one must not carry a stale reason from a previous revocation that never happened.
        CONSTRAINT CK_UserSession_RevokedConsistency CHECK
        (
            (RevokedAt IS NULL AND RevokedReason IS NULL) OR
            (RevokedAt IS NOT NULL AND RevokedReason IS NOT NULL)
        )
    );

    -- TR-SEC-13: the bulk revoke-on-ISP-lock and revoke-on-user-lock queries filter to a user's
    -- still-active sessions.
    CREATE INDEX IX_UserSession_UserId_RevokedAt ON sec.UserSession (UserId, RevokedAt);
END
GO

-- --------------------------------------------------------------------------------------
-- sec.TwoFactorChallenge — TR-SEC-04
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.TwoFactorChallenge', 'U') IS NULL
BEGIN
    CREATE TABLE sec.TwoFactorChallenge
    (
        ChallengeId    bigint            IDENTITY(1,1) NOT NULL,
        -- Opaque token returned to the caller after the first factor succeeds. The lookup key.
        ChallengeToken nvarchar(64)      NOT NULL,
        UserId         bigint            NOT NULL,
        Channel        nvarchar(20)      NOT NULL,
        -- Hash of the one-time code. NULL for the Totp channel: the code there is verified
        -- directly against sec.[User].TotpSecret, the portal never generates or stores it.
        CodeHash       char(64)          NULL,
        CreatedAt      datetimeoffset(7) NOT NULL,
        -- At most 5 minutes after CreatedAt (TR-SEC-04).
        ExpiresAt      datetimeoffset(7) NOT NULL,
        -- Set once verification succeeds. A consumed challenge can never be verified again.
        ConsumedAt     datetimeoffset(7) NULL,
        AttemptCount   int               NOT NULL CONSTRAINT DF_TwoFactorChallenge_AttemptCount DEFAULT (0),
        CONSTRAINT PK_TwoFactorChallenge PRIMARY KEY CLUSTERED (ChallengeId),
        CONSTRAINT UX_TwoFactorChallenge_ChallengeToken UNIQUE (ChallengeToken),
        CONSTRAINT FK_TwoFactorChallenge_User FOREIGN KEY (UserId) REFERENCES sec.[User] (UserId),
        CONSTRAINT CK_TwoFactorChallenge_Channel CHECK (Channel IN ('Totp', 'EmailOtp', 'SmsOtp')),
        CONSTRAINT CK_TwoFactorChallenge_ExpiresAfterCreated CHECK (ExpiresAt > CreatedAt),
        CONSTRAINT CK_TwoFactorChallenge_AttemptCount CHECK (AttemptCount >= 0),
        -- Totp never has a stored code; the generated channels always do.
        CONSTRAINT CK_TwoFactorChallenge_CodeHashByChannel CHECK
        (
            (Channel = 'Totp' AND CodeHash IS NULL) OR
            (Channel IN ('EmailOtp', 'SmsOtp') AND CodeHash IS NOT NULL)
        )
    );
END
GO

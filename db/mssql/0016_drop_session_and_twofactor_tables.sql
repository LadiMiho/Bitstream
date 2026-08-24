/*
    0016_drop_session_and_twofactor_tables.sql

    Sessions and two-factor authentication are now fully native ASP.NET Core Identity —
    SignInManager's own cookie authentication (Bitstream.Web/Program.cs) replaces the custom
    sec.UserSession + SessionAuthenticationHandler design, and Identity's own token providers
    (Authenticator/Email/Phone, dbo.UserTokens) replace sec.TwoFactorChallenge +
    ITotpService/ITotpSecretProtector. Neither table has a reason to keep existing.

    0014_drop_legacy_identity_tables.sql already dropped the FK from each of these tables to
    sec.[User] (step 1 of that script) without re-adding it (step 3 deliberately skips them) — so
    this script only needs to drop the tables themselves.

    Dev-only reset: no session/2FA-challenge data is preserved — none of it would still be valid
    against the new cookie-based sessions or Identity's own token providers anyway.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('sec.TwoFactorChallenge', 'U') IS NOT NULL
    DROP TABLE sec.TwoFactorChallenge;
GO

IF OBJECT_ID('sec.UserSession', 'U') IS NOT NULL
    DROP TABLE sec.UserSession;
GO

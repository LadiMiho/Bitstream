/*
    0012_totp_enrollment.sql
    TR-SEC-04 — distinguishes a TOTP secret that has merely been generated from one actually
    confirmed by the user.

    Before this, every account with a TotpSecret was assumed already enrolled, which only held
    because the sole account ever seeded that way (the development administrator) had its secret
    handed to the developer out of band, via a console log line. sec.[User].TotpConfirmedAt is
    set the moment a user first submits a valid code; while it is null, login shows the QR code
    instead of a bare code prompt, and the first valid code both confirms enrollment and signs
    the user in.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('sec.[User]') AND name = 'TotpConfirmedAt'
)
BEGIN
    ALTER TABLE sec.[User]
        ADD TotpConfirmedAt datetimeoffset(7) NULL;
END
GO

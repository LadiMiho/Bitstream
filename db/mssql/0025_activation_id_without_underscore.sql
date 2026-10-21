/*
    0025_activation_id_without_underscore.sql
    Activation request IDs become the ISP's ticket code directly followed by the number, with no
    separator: TRING001 instead of TRING_001 (and that is also what CRM receives as NAME).

    Only ops.usp_NextPrefixedIdentifier changes (it was created in 0018); the per-prefix counters
    carry on, so after TRING_003 the next request is TRING004. IDs already issued keep their
    underscore: a request's PublicId never changes once issued (0006).
    Numbers are still zero-padded to at least three digits: TRING001 ... TRING999, then TRING1000.
    Schema version 18.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE ops.usp_NextPrefixedIdentifier
    @Prefix     nvarchar(20),
    @Identifier nvarchar(32) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Prefix IS NULL OR LEN(@Prefix) = 0 OR @Prefix COLLATE Latin1_General_BIN LIKE '%[^A-Z]%'
        THROW 50002, 'Identifier prefix must be uppercase letters A-Z only.', 1;

    DECLARE @allocated TABLE (Value bigint);

    BEGIN TRANSACTION;

    UPDATE ops.PublicIdentifierPrefixCounter WITH (UPDLOCK, HOLDLOCK)
    SET NextValue = NextValue + 1,
        UpdatedAt = SYSDATETIMEOFFSET()
    OUTPUT deleted.NextValue INTO @allocated (Value)
    WHERE Prefix = @Prefix;

    IF NOT EXISTS (SELECT 1 FROM @allocated)
    BEGIN
        INSERT INTO ops.PublicIdentifierPrefixCounter (Prefix, NextValue) VALUES (@Prefix, 2);
        INSERT INTO @allocated (Value) VALUES (1);
    END

    COMMIT TRANSACTION;

    SELECT @Identifier = @Prefix +
        CASE WHEN Value < 1000 THEN RIGHT(N'00' + CONVERT(nvarchar(20), Value), 3) ELSE CONVERT(nvarchar(20), Value) END
    FROM @allocated;
END
GO

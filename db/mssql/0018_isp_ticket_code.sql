/*
    0018_isp_ticket_code.sql
    Per-ISP activation request identifiers: each ISP gets a unique ticket code (e.g. TRING),
    and its activation requests are numbered TRING_001, TRING_002, ... instead of drawing from
    the single ActivationRequest series in ops.PublicIdentifierSeries.

    The counter is keyed by the code itself, not by the ISP: if an ISP's code is later changed
    and the old code reused by another ISP, numbering continues where it left off, so an
    identifier can never be issued twice.

    Existing ISPs get no code here. Until one is set in ISP Administration, that ISP's
    activation requests are refused with a validation message.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('sec.Isp', 'TicketCode') IS NULL
BEGIN
    ALTER TABLE sec.Isp ADD TicketCode nvarchar(20) NULL;
END
GO

-- The prefix half of ^[A-Z]+_[0-9]+$ (TR-DAT-02d): uppercase letters only.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Isp_TicketCode' AND parent_object_id = OBJECT_ID('sec.Isp'))
BEGIN
    ALTER TABLE sec.Isp ADD CONSTRAINT CK_Isp_TicketCode
        CHECK (TicketCode IS NULL OR (LEN(TicketCode) > 0 AND TicketCode COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z]%'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Isp_TicketCode' AND object_id = OBJECT_ID('sec.Isp'))
BEGIN
    CREATE UNIQUE INDEX UX_Isp_TicketCode ON sec.Isp (TicketCode) WHERE TicketCode IS NOT NULL;
END
GO

IF OBJECT_ID('ops.PublicIdentifierPrefixCounter', 'U') IS NULL
BEGIN
    CREATE TABLE ops.PublicIdentifierPrefixCounter
    (
        Prefix    nvarchar(20)      NOT NULL,
        NextValue bigint            NOT NULL CONSTRAINT DF_PublicIdentifierPrefixCounter_NextValue DEFAULT (1),
        UpdatedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_PublicIdentifierPrefixCounter_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_PublicIdentifierPrefixCounter PRIMARY KEY CLUSTERED (Prefix),
        CONSTRAINT CK_PublicIdentifierPrefixCounter_Prefix CHECK (LEN(Prefix) > 0 AND Prefix COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z]%'),
        CONSTRAINT CK_PublicIdentifierPrefixCounter_NextValue CHECK (NextValue >= 1)
    );
END
GO

/*
    usp_NextPrefixedIdentifier
    Allocates the next identifier for a prefix, creating its counter on first use. UPDLOCK +
    HOLDLOCK serialise concurrent first allocations of the same prefix (the second waits on the
    key-range lock instead of racing the INSERT). Numbers are zero-padded to at least three
    digits: TRING_001 ... TRING_999, then TRING_1000.
*/
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

    SELECT @Identifier = @Prefix + N'_' +
        CASE WHEN Value < 1000 THEN RIGHT(N'00' + CONVERT(nvarchar(20), Value), 3) ELSE CONVERT(nvarchar(20), Value) END
    FROM @allocated;
END
GO

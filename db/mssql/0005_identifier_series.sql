/*
    0005_identifier_series.sql
    TRD 3.2 — generation of the public identifier <PREFIX>_<NUMBER>.

    Why a counter table and not a SQL Server SEQUENCE
    -------------------------------------------------
    TR-DAT-02b requires a gap-free, monotonically increasing integer starting at 1.
    A SEQUENCE is monotonic but NOT gap-free: values are handed out outside the caller's
    transaction, so a rollback, a cache flush or a service restart burns numbers. The only
    way to keep the series gap-free is to allocate inside the caller's transaction, which is
    what this counter table does: the UPDATE takes an exclusive row lock that is held until
    the caller commits, so a rolled-back submission returns its number to the series.

    The cost is that concurrent submissions of the same series are serialised for the
    duration of the transaction. That is acceptable here — the allocation is the last step
    before an INSERT, the transaction is short, and TR-NFR-03 sizes the system at 200
    concurrent users, not 200 concurrent submissions per second. If the series ever has to
    scale beyond that, TR-DAT-02b is the requirement that must be relaxed, not this design.

    Prefix values are environment configuration (TR-DAT-02a) and non-production must use a
    distinct prefix (TR-DAT-02e). The agreed values are TRD 11.4 open item 2 — the rows
    seeded below use placeholders that MUST be set per environment before go-live.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('ops.PublicIdentifierSeries', 'U') IS NULL
BEGIN
    CREATE TABLE ops.PublicIdentifierSeries
    (
        SeriesCode nvarchar(50)      NOT NULL,
        Prefix     nvarchar(10)      NOT NULL,
        NextValue  bigint            NOT NULL CONSTRAINT DF_PublicIdentifierSeries_NextValue DEFAULT (1),
        UpdatedAt  datetimeoffset(7) NOT NULL CONSTRAINT DF_PublicIdentifierSeries_UpdatedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_PublicIdentifierSeries PRIMARY KEY CLUSTERED (SeriesCode),
        -- TR-DAT-02d: the prefix half of ^[A-Z]+_[0-9]+$.
        CONSTRAINT CK_PublicIdentifierSeries_Prefix CHECK (Prefix NOT LIKE '%[^A-Z]%' AND LEN(Prefix) > 0),
        CONSTRAINT CK_PublicIdentifierSeries_NextValue CHECK (NextValue >= 1)
    );
END
GO

/*
    Placeholder prefixes. TR-DAT-06 requires complaint tickets to use a distinguishable
    series from activation requests, which is why there are three rows and not one.
    Replace the Prefix values per environment once open item 2 is answered:
        UPDATE ops.PublicIdentifierSeries SET Prefix = N'ISP' WHERE SeriesCode = N'ActivationRequest';
*/
MERGE ops.PublicIdentifierSeries AS target
USING
(
    VALUES
        (N'ActivationRequest',   N'ISP'),
        (N'ComplaintTicket',     N'TKT'),
        (N'ServiceChangeRequest', N'SCR')
) AS source (SeriesCode, Prefix)
    ON target.SeriesCode = source.SeriesCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SeriesCode, Prefix, NextValue) VALUES (source.SeriesCode, source.Prefix, 1);
GO

/*
    usp_NextPublicIdentifier
    Allocates the next identifier of a series. Must be called inside the caller's
    transaction (TR-DAT-01, TR-DAT-03): the row lock taken here is what makes the series
    gap-free and collision-free under concurrent submissions.
*/
CREATE OR ALTER PROCEDURE ops.usp_NextPublicIdentifier
    @SeriesCode  nvarchar(50),
    @Identifier  nvarchar(32) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @allocated TABLE (Prefix nvarchar(10), Value bigint);

    UPDATE ops.PublicIdentifierSeries
    SET NextValue = NextValue + 1,
        UpdatedAt = SYSDATETIMEOFFSET()
    OUTPUT deleted.Prefix, deleted.NextValue INTO @allocated (Prefix, Value)
    WHERE SeriesCode = @SeriesCode;

    IF NOT EXISTS (SELECT 1 FROM @allocated)
        THROW 50001, 'Unknown identifier series. Seed ops.PublicIdentifierSeries for this environment.', 1;

    -- TR-DAT-02c: never zero-padded, so the series is not capped at a fixed width.
    SELECT @Identifier = Prefix + N'_' + CONVERT(nvarchar(20), Value)
    FROM @allocated;
END
GO

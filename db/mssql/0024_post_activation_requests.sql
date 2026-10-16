/*
    0024_post_activation_requests.sql
    Post-activation requests: the ISP (or an administrator) picks a phone number, a level-3
    support classification (0023) and an optional note; the portal sends CRM's
    CREATE_TICKET_BYPHONENR operation through the outbox.

    portal.BiPhoneNumber
        Filled by BI, read-only for the portal. One row per phone number, e.g. TRING_123: the
        text before the first '_' is the ISP's Ticket Code (sec.Isp.TicketCode), which is how an
        ISP user is limited to their own numbers. BI's SQL login needs write access, e.g.:
            -- GRANT SELECT, INSERT, UPDATE, DELETE ON portal.BiPhoneNumber TO [<bi login>];

    portal.PostActivationRequest
        One row per submitted request. The three classification levels are copied (code + name)
        at submission, so renaming a classification later never changes a past request.

    Identifier series PostActivationRequest (PAR_1, PAR_2, ...).
    Schema version 17. The portal schema grants in 0008 already cover both tables.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.BiPhoneNumber', 'U') IS NULL
BEGIN
    CREATE TABLE portal.BiPhoneNumber
    (
        Phonenumber nvarchar(50) NOT NULL,
        CONSTRAINT PK_BiPhoneNumber PRIMARY KEY CLUSTERED (Phonenumber)
    );
END;
GO

-- Sample rows for a development database only:
-- INSERT INTO portal.BiPhoneNumber (Phonenumber) VALUES (N'TRING_123'), (N'TRING_124');

IF OBJECT_ID('portal.PostActivationRequest', 'U') IS NULL
BEGIN
    CREATE TABLE portal.PostActivationRequest
    (
        RequestId           bigint IDENTITY(1, 1) NOT NULL,
        PublicId            nvarchar(30)       NOT NULL,
        IspId               bigint             NOT NULL,
        Phonenumber         nvarchar(50)       NOT NULL,
        ClassificationId    bigint             NOT NULL,
        Classification1Code nvarchar(20)       NOT NULL,
        Classification1Name nvarchar(200)      NOT NULL,
        Classification2Code nvarchar(20)       NOT NULL,
        Classification2Name nvarchar(200)      NOT NULL,
        Classification3Code nvarchar(20)       NOT NULL,
        Classification3Name nvarchar(200)      NOT NULL,
        Note                nvarchar(4000)     NULL,
        Status              nvarchar(30)       NOT NULL,
        CrmTicketId         nvarchar(30)       NULL,
        CrmMessage          nvarchar(1000)     NULL,
        CreatedAt           datetimeoffset(7)  NOT NULL,
        CreatedBy           bigint             NULL,
        LastUpdatedAt       datetimeoffset(7)  NULL,
        CONSTRAINT PK_PostActivationRequest PRIMARY KEY CLUSTERED (RequestId),
        CONSTRAINT UX_PostActivationRequest_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_PostActivationRequest_Isp FOREIGN KEY (IspId) REFERENCES sec.Isp (IspId),
        CONSTRAINT FK_PostActivationRequest_Classification FOREIGN KEY (ClassificationId) REFERENCES portal.SupportClassification (ClassificationId)
    );

    CREATE INDEX IX_PostActivationRequest_Isp_Created ON portal.PostActivationRequest (IspId, CreatedAt DESC);
    CREATE INDEX IX_PostActivationRequest_Status ON portal.PostActivationRequest (Status);
END;
GO

MERGE ops.PublicIdentifierSeries AS target
USING (VALUES (N'PostActivationRequest', N'PAR')) AS source (SeriesCode, Prefix)
    ON target.SeriesCode = source.SeriesCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SeriesCode, Prefix, NextValue) VALUES (source.SeriesCode, source.Prefix, 1);
GO

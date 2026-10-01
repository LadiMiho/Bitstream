/*
    0019_package_offer.sql
    Each package + contract duration combination has its own CRM code, sent as CLASS_3 when the
    activation ticket is created (INT-CRM-02, BITSTREAM_TICKET_CREATE). portal.PackageOffer holds
    the combinations that may be ordered; it is created empty — insert one row per combination:

        INSERT portal.PackageOffer (PackageCode, ContractDurationMonths, OfferCode)
        VALUES (N'BITSTREAM_STD', 12, N'5100020013');

    Until a combination has a row, an activation request for it is refused and the form does not
    offer that contract duration for that package.

    portal.ActivationRequest.OfferCode records the code chosen at submission, so later edits to
    portal.PackageOffer never change what an existing request sent to CRM.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.PackageOffer', 'U') IS NULL
BEGIN
    CREATE TABLE portal.PackageOffer
    (
        PackageCode            nvarchar(50) NOT NULL,
        ContractDurationMonths int          NOT NULL,
        OfferCode              nvarchar(50) NOT NULL,
        IsActive               bit          NOT NULL CONSTRAINT DF_PackageOffer_IsActive DEFAULT (1),
        CONSTRAINT PK_PackageOffer PRIMARY KEY CLUSTERED (PackageCode, ContractDurationMonths),
        CONSTRAINT FK_PackageOffer_Package FOREIGN KEY (PackageCode) REFERENCES portal.Package (Code),
        CONSTRAINT FK_PackageOffer_ContractDuration FOREIGN KEY (ContractDurationMonths) REFERENCES portal.ContractDuration (Months),
        CONSTRAINT CK_PackageOffer_OfferCode CHECK (LEN(OfferCode) > 0)
    );

    CREATE UNIQUE INDEX UX_PackageOffer_OfferCode ON portal.PackageOffer (OfferCode);
END
GO

IF COL_LENGTH('portal.ActivationRequest', 'OfferCode') IS NULL
BEGIN
    ALTER TABLE portal.ActivationRequest ADD OfferCode nvarchar(50) NULL;
END
GO

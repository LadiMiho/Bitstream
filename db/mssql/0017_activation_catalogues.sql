/*
    0017_activation_catalogues.sql
    Moves the activation request form's reference lists — packages (TR-ACT-01), ticket
    classifications (TR-ACT-04) and contract durations (TRD 5.1) — from
    appsettings.json:Catalogues (Bitstream.Application.Configuration.CatalogueOptions) into
    tables, so they can be maintained without a release. LineTechnologies, IspNotifiableStatuses
    and ComplaintCategories are unaffected and stay configuration.

    Seed values below are exactly the values these three lists held in appsettings.json before
    this script; re-running is safe (MERGE), and later administrator edits are preserved the
    same way 0015_seed_role_baseline.sql preserves an administrator's later permission grants.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- --------------------------------------------------------------------------------------
-- portal.Package — TR-ACT-01. Tier drives service-change upgrade/downgrade eligibility
-- (TR-PAS-35, ServiceChangeRequestService).
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.Package', 'U') IS NULL
BEGIN
    CREATE TABLE portal.Package
    (
        Code     nvarchar(50)  NOT NULL,
        Name     nvarchar(200) NOT NULL,
        Tier     int           NOT NULL,
        IsActive bit           NOT NULL CONSTRAINT DF_Package_IsActive DEFAULT (1),
        CONSTRAINT PK_Package PRIMARY KEY CLUSTERED (Code)
    );
END
GO

-- --------------------------------------------------------------------------------------
-- portal.ActivationClassification — TR-ACT-04.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ActivationClassification', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ActivationClassification
    (
        Code      nvarchar(50)  NOT NULL,
        Name      nvarchar(200) NOT NULL,
        IsDefault bit           NOT NULL CONSTRAINT DF_ActivationClassification_IsDefault DEFAULT (0),
        IsActive  bit           NOT NULL CONSTRAINT DF_ActivationClassification_IsActive  DEFAULT (1),
        CONSTRAINT PK_ActivationClassification PRIMARY KEY CLUSTERED (Code)
    );

    -- TR-ACT-04: the activation form pre-selects one classification, so at most one row may claim it.
    CREATE UNIQUE INDEX UX_ActivationClassification_OneDefault ON portal.ActivationClassification (IsDefault) WHERE IsDefault = 1;
END
GO

-- --------------------------------------------------------------------------------------
-- portal.ContractDuration — TRD 5.1.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ContractDuration', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ContractDuration
    (
        Months   int          NOT NULL,
        Label    nvarchar(50) NOT NULL,
        IsActive bit          NOT NULL CONSTRAINT DF_ContractDuration_IsActive DEFAULT (1),
        CONSTRAINT PK_ContractDuration PRIMARY KEY CLUSTERED (Months),
        CONSTRAINT CK_ContractDuration_Months CHECK (Months > 0)
    );
END
GO

MERGE portal.Package AS target
USING
(
    VALUES
        (N'BITSTREAM_BASIC', N'Bitstream Basic',        10, CAST(1 AS bit)),
        (N'BITSTREAM_STD',   N'Bitstream Standard',     20, CAST(1 AS bit)),
        (N'BITSTREAM_PRO',   N'Bitstream Professional', 30, CAST(1 AS bit))
) AS source (Code, Name, Tier, IsActive)
    ON target.Code = source.Code
WHEN MATCHED THEN
    UPDATE SET Name = source.Name, Tier = source.Tier, IsActive = source.IsActive
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Name, Tier, IsActive) VALUES (source.Code, source.Name, source.Tier, source.IsActive);
GO

MERGE portal.ActivationClassification AS target
USING
(
    VALUES (N'REQUEST_FOR_ACTIVATION', N'Request for Activation', CAST(1 AS bit), CAST(1 AS bit))
) AS source (Code, Name, IsDefault, IsActive)
    ON target.Code = source.Code
WHEN MATCHED THEN
    UPDATE SET Name = source.Name, IsDefault = source.IsDefault, IsActive = source.IsActive
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Name, IsDefault, IsActive) VALUES (source.Code, source.Name, source.IsDefault, source.IsActive);
GO

MERGE portal.ContractDuration AS target
USING
(
    VALUES
        (12, N'12 months', CAST(1 AS bit)),
        (24, N'24 months', CAST(1 AS bit))
) AS source (Months, Label, IsActive)
    ON target.Months = source.Months
WHEN MATCHED THEN
    UPDATE SET Label = source.Label, IsActive = source.IsActive
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Months, Label, IsActive) VALUES (source.Months, source.Label, source.IsActive);
GO

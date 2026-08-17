/*
    0001_schemas_and_version.sql
    Bitstream Portal — schemas and the deployment version ledger.

    Every script in this folder is idempotent and re-runnable: TR-ARC-08 requires
    repeatable, scripted deployment, and the same script set is applied unchanged to
    Development, UAT and Production (TR-ARC-07).

    Run order is the numeric prefix. Deploy-Database.ps1 applies them in order and records
    each one in ops.SchemaVersion.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF SCHEMA_ID('sec') IS NULL
    EXEC('CREATE SCHEMA sec AUTHORIZATION dbo;');
GO

IF SCHEMA_ID('portal') IS NULL
    EXEC('CREATE SCHEMA portal AUTHORIZATION dbo;');
GO

IF SCHEMA_ID('ops') IS NULL
    EXEC('CREATE SCHEMA ops AUTHORIZATION dbo;');
GO

/*
    Applied-script ledger. BitstreamDbContext.ExpectedSchemaVersion is compared against
    MAX(SchemaVersion) at start-up so that an application deployed against an older or newer
    schema fails immediately instead of failing later on a missing column.
*/
IF OBJECT_ID('ops.SchemaVersion', 'U') IS NULL
BEGIN
    CREATE TABLE ops.SchemaVersion
    (
        SchemaVersionId int             IDENTITY(1,1) NOT NULL,
        ScriptName      nvarchar(200)   NOT NULL,
        SchemaVersion   int             NOT NULL,
        AppliedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_SchemaVersion_AppliedAt DEFAULT SYSDATETIMEOFFSET(),
        AppliedBy       nvarchar(128)   NOT NULL CONSTRAINT DF_SchemaVersion_AppliedBy DEFAULT SUSER_SNAME(),
        CONSTRAINT PK_SchemaVersion PRIMARY KEY CLUSTERED (SchemaVersionId),
        CONSTRAINT UX_SchemaVersion_ScriptName UNIQUE (ScriptName)
    );
END
GO

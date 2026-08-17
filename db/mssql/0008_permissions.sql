/*
    0008_permissions.sql
    Database grants for the application service account.

    The account the IIS application pool runs as is granted the minimum needed to run the
    portal. It is deliberately NOT db_owner and NOT db_datawriter across the board:
      * DELETE is granted nowhere. TR-DAT-07 and TR-SEC-24 are then true by construction
        rather than by convention, with the triggers in 0006 as the second line.
      * UPDATE on sec.AuditLog is denied outright.
      * DDL rights are not granted: schema changes are applied by the DBA running the
        numbered scripts, never by the application (ADR-0002).

    Set $(AppUser) when running: sqlcmd -v AppUser="BITSTREAM\svc_bitstream_app"
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF '$(AppUser)' = '' OR '$(AppUser)' LIKE '$%'
BEGIN
    RAISERROR('Set the AppUser variable, e.g. sqlcmd -v AppUser="DOMAIN\svc_account".', 16, 1);
    SET NOEXEC ON;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = '$(AppUser)')
    CREATE USER [$(AppUser)] FOR LOGIN [$(AppUser)];
GO

GRANT SELECT, INSERT, UPDATE ON SCHEMA::portal TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE ON SCHEMA::ops    TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE ON SCHEMA::sec    TO [$(AppUser)];

-- TR-SEC-24: append-only, enforced by permission as well as by trigger.
DENY UPDATE ON sec.AuditLog TO [$(AppUser)];

-- TR-DAT-07: no path from the application deletes a row anywhere.
DENY DELETE ON SCHEMA::portal TO [$(AppUser)];
DENY DELETE ON SCHEMA::sec    TO [$(AppUser)];
DENY DELETE ON SCHEMA::ops    TO [$(AppUser)];

GRANT EXECUTE ON ops.usp_NextPublicIdentifier TO [$(AppUser)];
GO

SET NOEXEC OFF;
GO

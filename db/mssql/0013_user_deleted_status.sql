/*
    0013_user_deleted_status.sql
    TR-DAT-07 (no physical delete): the User Administration screen's "Delete" action is a soft
    delete — User.Status gains a third value, 'Deleted'. A deleted user cannot authenticate and
    is hidden from search/browse by default, but the row itself, and every audit log, session
    and password-history row that references it, stay exactly as they are. Nothing here changes
    what a physical delete would have broken.

    Historical no-op as of 0014_drop_legacy_identity_tables.sql: sec.[User] (this script's
    target) no longer exists by the time this runs, having been replaced by dbo.Users,
    whose equivalent CK_Users_Status constraint is instead added by the identity EF
    migration (Identity/Migrations/20260824120000_InitialIdentitySchema.cs). Left in place,
    unmodified, purely as the schema-version history record described on
    BitstreamDbContext.ExpectedSchemaVersion; the guarded IF EXISTS below is false for a fresh
    database and simply does nothing.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_User_Status' AND parent_object_id = OBJECT_ID('sec.[User]')
)
AND NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = 'CK_User_Status' AND parent_object_id = OBJECT_ID('sec.[User]')
      AND definition LIKE '%Deleted%'
)
BEGIN
    ALTER TABLE sec.[User] DROP CONSTRAINT CK_User_Status;
    ALTER TABLE sec.[User] ADD CONSTRAINT CK_User_Status CHECK (Status IN ('Active', 'Locked', 'Deleted'));
END
GO

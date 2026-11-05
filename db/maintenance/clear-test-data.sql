/*
    clear-test-data.sql — TEST / DEVELOPMENT DATABASES ONLY

    Removes all the data entered while testing, so the database starts clean, and keeps the set-up.

    Deletes:
      - every ISP (sec.Isp) and every user except the administrator named in @AdminEmail, with
        their roles, claims, logins, tokens and password history (dbo.User*, sec.UserPasswordHistory);
      - every activation request, post-activation request, active line, complaint ticket,
        ticket comment and service change request (portal.*);
      - every CRM message (ops.IntegrationMessage), queued email (ops.Notification) and in-app
        notification (portal.UserNotification);
      - the audit log (sec.AuditLog)                                        [@DeleteAuditLog];
      - the BI phone numbers and the BI sync state (portal.BiPhoneNumber, ops.SyncState)
        — BI fills them again on its next run                               [@DeleteBiData];
      - the request numbering: TRING001, PAR_1, ... start again from 1, and the internal row
        IDs of the emptied tables restart at 1                              [@ResetNumbering].

    Keeps:
      - the administrator account (@AdminEmail), its password, role and authenticator key, so it
        signs in exactly as before; it is detached from any ISP;
      - packages, contract durations and package offers (portal.Package, portal.ContractDuration,
        portal.PackageOffer) and activation classifications (portal.ActivationClassification);
      - support classifications (portal.SupportClassification);
      - roles, permissions and notification rules (dbo.Roles, sec.Permission,
        sec.RolePermission, portal.NotificationRule);
      - the schema version and the Data Protection keys (ops.SchemaVersion,
        ops.DataProtectionKeys: deleting them would sign everyone out and break the
        administrator's authenticator).

    Safety:
      - Stops before changing anything if the administrator account is not found.
      - Runs in one transaction. With @Commit = 0 (the default) it only shows what it would
        delete and then rolls everything back. Set @Commit = 1 to really delete.
      - ISPs, requests, tickets, comments and the audit log are protected by delete-blocking
        triggers (db/mssql/0006_integrity_guards.sql). The script disables them only for its own
        deletes, inside the transaction, and re-enables them before it ends, also on error.
      - Run it as a DBA login: the application's account is denied DELETE on purpose.
      - Not part of db/mssql, so Deploy-Database.ps1 never runs it.

    Run in SSMS (or sqlcmd) against the BitstreamPortal database:
        sqlcmd -S <server> -d BitstreamPortal -i clear-test-data.sql
    Stop the Web and API sites first, so nothing writes while it runs.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;  -- required: some of these tables have filtered indexes

DECLARE @Commit         bit           = 0;                        -- 0 = dry run (show counts, roll back); 1 = delete for real
DECLARE @AdminEmail     nvarchar(256) = N'admin@bitstream.local'; -- the one account that is kept
DECLARE @DeleteAuditLog bit           = 1;                        -- 1 = empty the audit log too
DECLARE @DeleteBiData   bit           = 1;                        -- 1 = also delete BI phone numbers and BI sync state
DECLARE @ResetNumbering bit           = 1;                        -- 1 = request numbers and row IDs start again from 1

PRINT CONCAT('Database: ', DB_NAME(), ' on ', @@SERVERNAME, '   Mode: ', IIF(@Commit = 1, 'DELETE', 'DRY RUN (rolled back)'));

DECLARE @AdminId bigint = (SELECT Id FROM dbo.Users WHERE NormalizedEmail = UPPER(@AdminEmail));
IF @AdminId IS NULL
BEGIN
    RAISERROR('The administrator %s was not found in dbo.Users. Nothing was deleted.', 16, 1, @AdminEmail);
    RETURN;
END;
PRINT CONCAT('Keeping administrator: ', @AdminEmail, ' (Id ', @AdminId, ')');

BEGIN TRY
    BEGIN TRANSACTION;

    -- Delete-blocking triggers off, for this transaction only (rolled back with it on error).
    DISABLE TRIGGER portal.TR_TicketComment_NoDelete        ON portal.TicketComment;
    DISABLE TRIGGER portal.TR_ServiceChangeRequest_NoDelete ON portal.ServiceChangeRequest;
    DISABLE TRIGGER portal.TR_ComplaintTicket_NoDelete      ON portal.ComplaintTicket;
    DISABLE TRIGGER portal.TR_ActivationRequest_NoDelete    ON portal.ActivationRequest;
    DISABLE TRIGGER sec.TR_Isp_NoDelete                     ON sec.Isp;
    IF @DeleteAuditLog = 1
        DISABLE TRIGGER sec.TR_AuditLog_AppendOnly ON sec.AuditLog;

    -- 1. Messages and notifications ----------------------------------------------------------
    DELETE FROM ops.IntegrationMessage;
    PRINT CONCAT('CRM messages deleted:                  ', @@ROWCOUNT);
    DELETE FROM ops.Notification;
    PRINT CONCAT('Queued emails deleted:                 ', @@ROWCOUNT);
    DELETE FROM portal.UserNotification;
    PRINT CONCAT('In-app notifications deleted:          ', @@ROWCOUNT);

    -- 2. Tickets, requests and lines (children before parents) ------------------------------
    DELETE FROM portal.TicketComment;
    PRINT CONCAT('Ticket comments deleted:               ', @@ROWCOUNT);
    DELETE FROM portal.ServiceChangeRequest;
    PRINT CONCAT('Service change requests deleted:       ', @@ROWCOUNT);
    DELETE FROM portal.ComplaintTicket;
    PRINT CONCAT('Complaint tickets deleted:             ', @@ROWCOUNT);
    DELETE FROM portal.PostActivationRequest;
    PRINT CONCAT('Post-activation requests deleted:      ', @@ROWCOUNT);
    DELETE FROM portal.ActivationRequest;
    PRINT CONCAT('Activation requests deleted:           ', @@ROWCOUNT);
    DELETE FROM portal.ActiveLine;
    PRINT CONCAT('Active lines deleted:                  ', @@ROWCOUNT);

    IF @DeleteBiData = 1
    BEGIN
        DELETE FROM portal.BiPhoneNumber;
        PRINT CONCAT('BI phone numbers deleted:              ', @@ROWCOUNT);
        DELETE FROM ops.SyncState;
        PRINT CONCAT('BI sync state rows deleted:            ', @@ROWCOUNT);
    END;

    -- 3. Audit log ---------------------------------------------------------------------------
    IF @DeleteAuditLog = 1
    BEGIN
        DELETE FROM sec.AuditLog;
        PRINT CONCAT('Audit log entries deleted:             ', @@ROWCOUNT);
    END;

    -- 4. Users other than the administrator --------------------------------------------------
    DELETE FROM sec.UserPasswordHistory WHERE UserId <> @AdminId;
    PRINT CONCAT('Password history rows deleted:         ', @@ROWCOUNT);
    DELETE FROM dbo.UserTokens WHERE UserId <> @AdminId;
    DELETE FROM dbo.UserLogins WHERE UserId <> @AdminId;
    DELETE FROM dbo.UserClaims WHERE UserId <> @AdminId;
    DELETE FROM dbo.UserRoles  WHERE UserId <> @AdminId;
    DELETE FROM dbo.Users      WHERE Id     <> @AdminId;
    PRINT CONCAT('Users deleted:                         ', @@ROWCOUNT);

    UPDATE dbo.Users SET IspId = NULL WHERE Id = @AdminId AND IspId IS NOT NULL;

    -- 5. ISPs --------------------------------------------------------------------------------
    DELETE FROM sec.Isp;
    PRINT CONCAT('ISPs deleted:                          ', @@ROWCOUNT);

    -- 6. Numbering ---------------------------------------------------------------------------
    IF @ResetNumbering = 1
    BEGIN
        DELETE FROM ops.PublicIdentifierPrefixCounter;   -- per ISP code: TRING001, FIBER001, ...
        UPDATE ops.PublicIdentifierSeries SET NextValue = 1, UpdatedAt = SYSDATETIMEOFFSET();  -- PAR_1, ...

        -- Row IDs of the tables emptied above start again at 1 (tables never used are left as they are).
        DECLARE @Table nvarchar(300);
        DECLARE reseed CURSOR LOCAL FAST_FORWARD FOR
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(ic.object_id)) + N'.' + QUOTENAME(OBJECT_NAME(ic.object_id))
            FROM sys.identity_columns ic
            WHERE ic.last_value IS NOT NULL
              AND ic.object_id IN (
                  OBJECT_ID(N'ops.IntegrationMessage'), OBJECT_ID(N'ops.Notification'), OBJECT_ID(N'portal.UserNotification'),
                  OBJECT_ID(N'portal.TicketComment'), OBJECT_ID(N'portal.ServiceChangeRequest'), OBJECT_ID(N'portal.ComplaintTicket'),
                  OBJECT_ID(N'portal.PostActivationRequest'), OBJECT_ID(N'portal.ActivationRequest'), OBJECT_ID(N'portal.ActiveLine'),
                  OBJECT_ID(N'portal.BiPhoneNumber'), OBJECT_ID(N'sec.AuditLog'), OBJECT_ID(N'sec.UserPasswordHistory'),
                  OBJECT_ID(N'sec.Isp'))
              AND NOT EXISTS (SELECT 1 FROM sys.partitions p WHERE p.object_id = ic.object_id AND p.index_id IN (0, 1) AND p.rows > 0);
        OPEN reseed;
        FETCH NEXT FROM reseed INTO @Table;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            DBCC CHECKIDENT (@Table, RESEED, 0) WITH NO_INFOMSGS;
            FETCH NEXT FROM reseed INTO @Table;
        END;
        CLOSE reseed;
        DEALLOCATE reseed;
        PRINT 'Request numbering and row IDs reset.';
    END;

    -- Protection back on before the transaction ends.
    ENABLE TRIGGER portal.TR_TicketComment_NoDelete        ON portal.TicketComment;
    ENABLE TRIGGER portal.TR_ServiceChangeRequest_NoDelete ON portal.ServiceChangeRequest;
    ENABLE TRIGGER portal.TR_ComplaintTicket_NoDelete      ON portal.ComplaintTicket;
    ENABLE TRIGGER portal.TR_ActivationRequest_NoDelete    ON portal.ActivationRequest;
    ENABLE TRIGGER sec.TR_Isp_NoDelete                     ON sec.Isp;
    ENABLE TRIGGER sec.TR_AuditLog_AppendOnly              ON sec.AuditLog;

    -- What is kept (one result row).
    SELECT
        (SELECT COUNT(*) FROM dbo.Users)                       AS Users_Kept,
        (SELECT COUNT(*) FROM portal.Package)                  AS Packages,
        (SELECT COUNT(*) FROM portal.ContractDuration)         AS ContractDurations,
        (SELECT COUNT(*) FROM portal.PackageOffer)             AS PackageOffers,
        (SELECT COUNT(*) FROM portal.ActivationClassification) AS ActivationClassifications,
        (SELECT COUNT(*) FROM portal.SupportClassification)    AS SupportClassifications,
        (SELECT COUNT(*) FROM portal.NotificationRule)         AS NotificationRules,
        (SELECT COUNT(*) FROM dbo.Roles)                       AS Roles,
        (SELECT COUNT(*) FROM sec.Permission)                  AS Permissions;

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'Committed.';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'Dry run: nothing was deleted. Set @Commit = 1 and run again to delete.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;  -- also undoes the DISABLE TRIGGER statements

    -- Belt and braces: make sure the protection is on even if the rollback did not cover it.
    ENABLE TRIGGER portal.TR_TicketComment_NoDelete        ON portal.TicketComment;
    ENABLE TRIGGER portal.TR_ServiceChangeRequest_NoDelete ON portal.ServiceChangeRequest;
    ENABLE TRIGGER portal.TR_ComplaintTicket_NoDelete      ON portal.ComplaintTicket;
    ENABLE TRIGGER portal.TR_ActivationRequest_NoDelete    ON portal.ActivationRequest;
    ENABLE TRIGGER sec.TR_Isp_NoDelete                     ON sec.Isp;
    ENABLE TRIGGER sec.TR_AuditLog_AppendOnly              ON sec.AuditLog;

    PRINT 'Failed, nothing was deleted:';
    THROW;
END CATCH;

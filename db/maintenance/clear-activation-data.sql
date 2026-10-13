/*
    clear-activation-data.sql — TEST / DEVELOPMENT DATABASES ONLY

    Deletes:
      1. every activation request (portal.ActivationRequest) and its related data:
         - the CRM messages for those requests, both directions (ops.IntegrationMessage):
           Business Partner, ticket, operator confirmation, and CRM's status events,
           matched by portal ID or CRM ticket number;
         - notifications about those requests (ops.Notification);
         - their audit trail (sec.AuditLog, EntityType 'ActivationRequest')  [@DeleteAuditTrail];
         - the request numbering, so the next request of each ISP is <CODE>_001 again
           (ops.PublicIdentifierPrefixCounter)                              [@ResetNumbering];
      2. the whole catalogue: package offers (portal.PackageOffer), packages (portal.Package)
         and contract durations (portal.ContractDuration), and their audit trail
         [@DeleteAuditTrail].

    Keeps: ISPs, users, roles, classifications, complaint tickets, active lines.

    Safety:
      - Runs in one transaction. With @Commit = 0 (the default) it only shows what it would
        delete and then rolls everything back. Set @Commit = 1 to really delete.
      - Activation requests and the audit log are protected by delete-blocking triggers
        (db/mssql/0006_integrity_guards.sql). The script disables them only for its own
        deletes and re-enables them before it ends, also on error.
      - Not part of db/mssql, so Deploy-Database.ps1 never runs it.

    Run in SSMS (or sqlcmd) against the BitstreamPortal database:
        sqlcmd -S <server> -d BitstreamPortal -i clear-activation-data.sql
    Afterwards, re-create packages, durations and package offers in the portal
    (Activation Requests > Catalogue / Package offers) before submitting new requests.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit           bit = 0;  -- 0 = dry run (show counts, roll back); 1 = delete for real
DECLARE @DeleteAuditTrail bit = 1;  -- 1 = also delete the audit entries of what is deleted
DECLARE @ResetNumbering   bit = 1;  -- 1 = restart request numbers at <CODE>_001

PRINT CONCAT('Database: ', DB_NAME(), ' on ', @@SERVERNAME, '   Mode: ', IIF(@Commit = 1, 'DELETE', 'DRY RUN (rolled back)'));

-- The identifiers CRM may have used for these requests: portal IDs and CRM ticket numbers.
DECLARE @Identifiers TABLE (Value nvarchar(64) PRIMARY KEY);
INSERT INTO @Identifiers (Value)
SELECT PublicId FROM portal.ActivationRequest
UNION
SELECT CrmTicketId FROM portal.ActivationRequest WHERE CrmTicketId IS NOT NULL;

DECLARE @RequestIds TABLE (RequestId bigint PRIMARY KEY);
INSERT INTO @RequestIds (RequestId) SELECT RequestId FROM portal.ActivationRequest;

BEGIN TRY
    BEGIN TRANSACTION;

    DISABLE TRIGGER portal.TR_ActivationRequest_NoDelete ON portal.ActivationRequest;
    IF @DeleteAuditTrail = 1
        DISABLE TRIGGER sec.TR_AuditLog_AppendOnly ON sec.AuditLog;

    -- 1. Activation requests and related data -------------------------------------------
    DELETE m FROM ops.IntegrationMessage m
    WHERE m.RelatedPublicId IN (SELECT Value FROM @Identifiers)
       OR m.InterfaceCode IN (N'INT-CRM-01', N'INT-CRM-02', N'INT-CRM-10');
    PRINT CONCAT('CRM messages deleted:             ', @@ROWCOUNT);

    DELETE n FROM ops.Notification n
    WHERE n.RelatedEntityType = N'ActivationRequest'
       OR n.RelatedEntityPublicId IN (SELECT Value FROM @Identifiers);
    PRINT CONCAT('Notifications deleted:            ', @@ROWCOUNT);

    IF @DeleteAuditTrail = 1
    BEGIN
        DELETE a FROM sec.AuditLog a
        WHERE a.EntityType = N'ActivationRequest'
          AND a.EntityId IN (SELECT CAST(RequestId AS nvarchar(64)) FROM @RequestIds);
        PRINT CONCAT('Activation audit entries deleted: ', @@ROWCOUNT);
    END;

    DELETE FROM portal.ActivationRequest;
    PRINT CONCAT('Activation requests deleted:      ', @@ROWCOUNT);

    IF @ResetNumbering = 1
    BEGIN
        UPDATE ops.PublicIdentifierPrefixCounter SET NextValue = 1, UpdatedAt = SYSDATETIMEOFFSET();
        PRINT CONCAT('Request numbering reset (codes):  ', @@ROWCOUNT);
    END;

    -- 2. Catalogue: offers first (they reference packages and durations) ------------------
    DELETE FROM portal.PackageOffer;
    PRINT CONCAT('Package offers deleted:           ', @@ROWCOUNT);

    DELETE FROM portal.Package;
    PRINT CONCAT('Packages deleted:                 ', @@ROWCOUNT);

    DELETE FROM portal.ContractDuration;
    PRINT CONCAT('Contract durations deleted:       ', @@ROWCOUNT);

    IF @DeleteAuditTrail = 1
    BEGIN
        DELETE FROM sec.AuditLog WHERE EntityType IN (N'PackageOffer', N'Package', N'ContractDuration');
        PRINT CONCAT('Catalogue audit entries deleted:  ', @@ROWCOUNT);
    END;

    -- Protection back on before the transaction ends.
    ENABLE TRIGGER portal.TR_ActivationRequest_NoDelete ON portal.ActivationRequest;
    ENABLE TRIGGER sec.TR_AuditLog_AppendOnly ON sec.AuditLog;

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
    ENABLE TRIGGER portal.TR_ActivationRequest_NoDelete ON portal.ActivationRequest;
    ENABLE TRIGGER sec.TR_AuditLog_AppendOnly ON sec.AuditLog;

    PRINT 'Failed, nothing was deleted:';
    THROW;
END CATCH;

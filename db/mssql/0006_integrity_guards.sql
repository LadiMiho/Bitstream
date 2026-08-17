/*
    0006_integrity_guards.sql
    Database-level enforcement of the rules the TRD states as absolute.

    These are defence in depth, not the primary control: the application never issues these
    statements. They exist because TR-SEC-24 ("no interface or API may permit modification
    or deletion of audit entries") and TR-DAT-07 ("no entity may be physically deleted")
    are claims about the system, not about one code path — an ad-hoc statement from a
    support session has to fail too.

    The application service account should additionally be denied DELETE on these tables;
    see 0008_permissions.sql.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- TR-SEC-24: the audit store is append-only.
CREATE OR ALTER TRIGGER sec.TR_AuditLog_AppendOnly
ON sec.AuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50010, 'sec.AuditLog is append-only (TR-SEC-24). Update and delete are not permitted.', 1;
END
GO

-- TR-PAS-27: a comment is immutable once saved. Only the replication bookkeeping columns
-- may change, so that a failed CRM replication can be retried and marked (TR-PAS-28).
CREATE OR ALTER TRIGGER portal.TR_TicketComment_Immutable
ON portal.TicketComment
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF UPDATE(Body) OR UPDATE(AuthorUserId) OR UPDATE(AuthorType) OR UPDATE(TicketId) OR UPDATE(CreatedAt)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50011, 'portal.TicketComment is immutable once saved (TR-PAS-27). Only CrmSyncStatus and CrmCommentId may be updated.', 1;
    END
END
GO

-- TR-PAS-27: comments are never removed, only made read-only when the ticket terminates.
CREATE OR ALTER TRIGGER portal.TR_TicketComment_NoDelete
ON portal.TicketComment
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50012, 'portal.TicketComment rows are never deleted (TR-DAT-07, TR-PAS-27).', 1;
END
GO

/*
    TR-DAT-07 / TR-SEC-11: no physical delete of business or identity data. Deactivation is
    expressed through a status field.

    ops.IntegrationMessage and ops.Notification are deliberately NOT covered: they are
    operational stores subject to archival (TR-DAT-10), and the archival job moves rows out.
    The retention periods that govern that job are TRD 11.4 open item 10, so the archival
    job and its target tables are not created yet.
*/
CREATE OR ALTER TRIGGER sec.TR_Isp_NoDelete
ON sec.Isp
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50013, 'sec.Isp rows are never deleted (TR-DAT-07). Set Status = ''Locked'' instead.', 1;
END
GO

CREATE OR ALTER TRIGGER sec.TR_User_NoDelete
ON sec.[User]
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50014, 'sec.[User] rows are never deleted (TR-SEC-11, TR-SEC-12). Set Status = ''Locked'' instead.', 1;
END
GO

CREATE OR ALTER TRIGGER portal.TR_ActivationRequest_NoDelete
ON portal.ActivationRequest
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50015, 'portal.ActivationRequest rows are never deleted (TR-DAT-07).', 1;
END
GO

CREATE OR ALTER TRIGGER portal.TR_ComplaintTicket_NoDelete
ON portal.ComplaintTicket
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50016, 'portal.ComplaintTicket rows are never deleted (TR-DAT-07).', 1;
END
GO

CREATE OR ALTER TRIGGER portal.TR_ServiceChangeRequest_NoDelete
ON portal.ServiceChangeRequest
INSTEAD OF DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 50017, 'portal.ServiceChangeRequest rows are never deleted (TR-DAT-07).', 1;
END
GO

-- TR-DAT-04: the public identifier is immutable once issued.
CREATE OR ALTER TRIGGER portal.TR_ActivationRequest_PublicIdImmutable
ON portal.ActivationRequest
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF UPDATE(PublicId)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50018, 'portal.ActivationRequest.PublicId is immutable once issued (TR-DAT-04).', 1;
    END
END
GO

CREATE OR ALTER TRIGGER portal.TR_ComplaintTicket_PublicIdImmutable
ON portal.ComplaintTicket
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    IF UPDATE(PublicId)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 50019, 'portal.ComplaintTicket.PublicId is immutable once issued (TR-DAT-04).', 1;
    END
END
GO

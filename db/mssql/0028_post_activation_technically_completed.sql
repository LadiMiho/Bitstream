/*
    0028_post_activation_technically_completed.sql
    Post-activation requests follow the CRM ticket to its closure:
      * a request whose ticket CRM created is now "In process" (status InProcess); rows saved
        before this as Submitted are converted;
      * CRM closes it with the inbound event TECHNICALLY_COMPLETED, addressed to the ticket number:
        the status becomes TechnicallyCompleted and CRM's reason and the time are kept in the new
        columns CompletionReason and CompletedAt;
      * CrmTicketId is indexed, since the event finds the request by it.
    Safe to re-run. Schema version 21.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF COL_LENGTH('portal.PostActivationRequest', 'CompletionReason') IS NULL
    ALTER TABLE portal.PostActivationRequest ADD CompletionReason nvarchar(1000) NULL;
GO

IF COL_LENGTH('portal.PostActivationRequest', 'CompletedAt') IS NULL
    ALTER TABLE portal.PostActivationRequest ADD CompletedAt datetimeoffset(7) NULL;
GO

UPDATE portal.PostActivationRequest SET Status = 'InProcess' WHERE Status = 'Submitted';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID('portal.PostActivationRequest') AND name = 'IX_PostActivationRequest_CrmTicketId')
    CREATE INDEX IX_PostActivationRequest_CrmTicketId ON portal.PostActivationRequest (CrmTicketId) WHERE CrmTicketId IS NOT NULL;
GO

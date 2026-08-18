/*
    0011_post_activation_support.sql
    TRD 6 — post-activation support.

    portal.ComplaintTicket gains three columns for the auto-confirmation engine (TR-PAS-21a/b):
    the anchor timestamp the reminder and auto-confirm clocks count working days from, and one
    slot per configured reminder (the default configuration sends exactly two, at day 2 and
    day 4 — see appsettings.json TicketClosure:ReminderAfterWorkingDays).

    ops.SyncState is one row per scheduled sync job (currently only the BI active-lines sync,
    TR-PAS-03/07); ChangeMarker is the incremental cursor, the rest is what
    GET /api/v1/ops/bi/active-lines/sync/status reports.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('portal.ComplaintTicket') AND name = 'ClearingCodeAppliedAt'
)
BEGIN
    ALTER TABLE portal.ComplaintTicket
        ADD ClearingCodeAppliedAt datetimeoffset(7) NULL,
            Reminder2SentAt       datetimeoffset(7) NULL,
            Reminder4SentAt       datetimeoffset(7) NULL;
END
GO

IF OBJECT_ID('ops.SyncState', 'U') IS NULL
BEGIN
    CREATE TABLE ops.SyncState
    (
        SyncKey              nvarchar(50)      NOT NULL,
        LastRunAt            datetimeoffset(7) NULL,
        LastSuccessfulSyncAt datetimeoffset(7) NULL,
        ConsecutiveFailures  int               NOT NULL CONSTRAINT DF_SyncState_ConsecutiveFailures DEFAULT (0),
        ChangeMarker         nvarchar(200)     NULL,
        CONSTRAINT PK_SyncState PRIMARY KEY CLUSTERED (SyncKey),
        CONSTRAINT CK_SyncState_ConsecutiveFailures CHECK (ConsecutiveFailures >= 0)
    );
END
GO

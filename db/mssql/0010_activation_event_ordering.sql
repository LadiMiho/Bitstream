/*
    0010_activation_event_ordering.sql
    TRD 7.3.2 / TR-INT-25: an inbound CRM event older than the last one already applied to a
    ticket must be discarded, not applied. That requires remembering, per activation request,
    the occurredAt of the last event actually applied — CreatedAt on ops.IntegrationMessage is
    when the portal received the event, not when CRM says it happened, so it cannot answer this
    on its own.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('portal.ActivationRequest') AND name = 'LastAppliedEventAt'
)
BEGIN
    ALTER TABLE portal.ActivationRequest
        ADD LastAppliedEventAt datetimeoffset(7) NULL;
END
GO

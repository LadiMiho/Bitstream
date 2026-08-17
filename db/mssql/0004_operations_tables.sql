/*
    0004_operations_tables.sql
    TRD 3.1 entities: Notification, IntegrationMessage.

    ops.IntegrationMessage is both the outbox (TR-ARC-03) and the inbox (TR-INT-24):
    one store, one dead-letter mechanism, one replay path, distinguished by Direction.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- --------------------------------------------------------------------------------------
-- ops.Notification — TRD 3.1, TRD 8. Delivery log.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('ops.Notification', 'U') IS NULL
BEGIN
    CREATE TABLE ops.Notification
    (
        NotificationId        bigint            IDENTITY(1,1) NOT NULL,
        TemplateCode          nvarchar(100)     NOT NULL,
        -- Resolved recipients after distribution-group expansion (TR-NTF-02).
        Recipients            nvarchar(4000)    NOT NULL,
        Subject               nvarchar(500)     NOT NULL,
        BodyRendered          nvarchar(max)     NOT NULL,
        RelatedEntityType     nvarchar(100)     NOT NULL,
        RelatedEntityId       bigint            NULL,
        RelatedEntityPublicId nvarchar(32)      NULL,
        [Status]              nvarchar(20)      NOT NULL CONSTRAINT DF_Notification_Status DEFAULT ('Pending'),
        Attempts              int               NOT NULL CONSTRAINT DF_Notification_Attempts DEFAULT (0),
        LastError             nvarchar(2000)    NULL,
        CreatedAt             datetimeoffset(7) NOT NULL CONSTRAINT DF_Notification_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        SentAt                datetimeoffset(7) NULL,
        CorrelationId         nvarchar(64)      NULL,
        CONSTRAINT PK_Notification PRIMARY KEY CLUSTERED (NotificationId),
        CONSTRAINT CK_Notification_Status   CHECK ([Status] IN ('Pending', 'Sent', 'Failed')),
        CONSTRAINT CK_Notification_Attempts CHECK (Attempts >= 0)
    );

    CREATE INDEX IX_Notification_Status_CreatedAt ON ops.Notification ([Status], CreatedAt);
    CREATE INDEX IX_Notification_RelatedEntity    ON ops.Notification (RelatedEntityType, RelatedEntityId);
END
GO

-- --------------------------------------------------------------------------------------
-- ops.IntegrationMessage — TRD 3.1. Outbox and inbox.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('ops.IntegrationMessage', 'U') IS NULL
BEGIN
    CREATE TABLE ops.IntegrationMessage
    (
        MessageId       bigint            IDENTITY(1,1) NOT NULL,
        Direction       nvarchar(10)      NOT NULL,
        TargetSystem    nvarchar(10)      NOT NULL,
        -- Interface code from TRD 7.1, e.g. INT-CRM-02.
        InterfaceCode   nvarchar(30)      NOT NULL,
        MessageType     nvarchar(50)      NULL,
        -- TR-INT-24: raw payload as sent or as received, before any mapping.
        Payload         nvarchar(max)     NOT NULL,
        IdempotencyKey  nvarchar(100)     NOT NULL,
        [Status]        nvarchar(20)      NOT NULL CONSTRAINT DF_IntegrationMessage_Status DEFAULT ('Pending'),
        Attempts        int               NOT NULL CONSTRAINT DF_IntegrationMessage_Attempts DEFAULT (0),
        LastError       nvarchar(2000)    NULL,
        NextRetryAt     datetimeoffset(7) NULL,
        RelatedPublicId nvarchar(32)      NULL,
        CorrelationId   nvarchar(64)      NOT NULL,
        CreatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_IntegrationMessage_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        ProcessedAt     datetimeoffset(7) NULL,
        -- TR-INT-25: stored so a repeated eventId returns the original outcome.
        ResponsePayload nvarchar(max)     NULL,
        CONSTRAINT PK_IntegrationMessage PRIMARY KEY CLUSTERED (MessageId),
        CONSTRAINT CK_IntegrationMessage_Direction    CHECK (Direction IN ('Outbound', 'Inbound')),
        CONSTRAINT CK_IntegrationMessage_TargetSystem CHECK (TargetSystem IN ('Crm', 'Bi', 'Sap', 'Smtp')),
        CONSTRAINT CK_IntegrationMessage_Status       CHECK ([Status] IN ('Pending', 'InFlight', 'Succeeded', 'Failed', 'DeadLettered')),
        CONSTRAINT CK_IntegrationMessage_Attempts     CHECK (Attempts >= 0)
    );

    -- TR-INT-03 / TR-INT-25: exactly-once per key, per direction, per interface.
    CREATE UNIQUE INDEX UX_IntegrationMessage_Idempotency
        ON ops.IntegrationMessage (Direction, TargetSystem, InterfaceCode, IdempotencyKey);

    -- Dispatcher claim query (TR-ARC-03) and dead-letter listing (TR-INT-05).
    CREATE INDEX IX_IntegrationMessage_Status_NextRetryAt ON ops.IntegrationMessage ([Status], NextRetryAt);
    CREATE INDEX IX_IntegrationMessage_RelatedPublicId    ON ops.IntegrationMessage (RelatedPublicId);
    CREATE INDEX IX_IntegrationMessage_CorrelationId      ON ops.IntegrationMessage (CorrelationId);
END
GO

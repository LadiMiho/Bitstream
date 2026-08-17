/*
    0003_portal_tables.sql
    TRD 3.1 entities: ActivationRequest, ActiveLine, ComplaintTicket, TicketComment,
    ServiceChangeRequest.

    Status vocabularies
      * ActivationRequest.Status is constrained: TRD 5.3 defines the state machine in full.
      * ComplaintTicket.Status and ServiceChangeRequest.Status are NOT constrained: the CRM
        status list is configurable (TR-PAS-16) and has not been supplied (TRD 11.4 open
        item 4). A CHECK constraint here would have to be altered every time CRM adds a
        status, which TR-PAS-16 explicitly forbids. Unknown values are rejected at the
        inbound API with 422 (TR-INT-27), which is where the vocabulary is validated.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- --------------------------------------------------------------------------------------
-- portal.ActivationRequest — TRD 3.1, TRD 5
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ActivationRequest', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ActivationRequest
    (
        RequestId              bigint            IDENTITY(1,1) NOT NULL,
        -- TR-DAT-02: <PREFIX>_<NUMBER>, variable length, never zero-padded.
        PublicId               nvarchar(32)      NOT NULL,
        IspId                  bigint            NOT NULL,
        PackageCode            nvarchar(50)      NOT NULL,
        LocationRaw            nvarchar(1000)    NOT NULL,
        LocationLat            decimal(9,6)      NOT NULL,
        LocationLng            decimal(9,6)      NOT NULL,
        Classification         nvarchar(50)      NOT NULL,
        ContractDurationMonths int               NOT NULL,
        Comments               nvarchar(2000)    NULL,
        [Status]               nvarchar(40)      NOT NULL,
        CrmTicketId            nvarchar(50)      NULL,
        CrmCustomerId          nvarchar(50)      NULL,
        Bp                     nvarchar(50)      NULL,
        SalesOrderId           nvarchar(50)      NULL,
        -- TR-INT-11: nullable until populated; its absence must not block the flow.
        FinancialCode          nvarchar(50)      NULL,
        StatusReason           nvarchar(1000)    NULL,
        CreatedAt              datetimeoffset(7) NOT NULL CONSTRAINT DF_ActivationRequest_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CreatedBy              bigint            NULL,
        LastUpdatedAt          datetimeoffset(7) NULL,
        CONSTRAINT PK_ActivationRequest PRIMARY KEY CLUSTERED (RequestId),
        -- TR-DAT-03 / TR-DAT-04: unique and immutable once issued.
        CONSTRAINT UX_ActivationRequest_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_ActivationRequest_Isp FOREIGN KEY (IspId) REFERENCES sec.Isp (IspId),
        /*
            TR-DAT-02d requires ^[A-Z]+_[0-9]+$. T-SQL LIKE has no anchored quantifiers, so
            this is the closest enforceable approximation: uppercase letters and digits and
            a single underscore only, starting with a letter, ending with a digit. The exact
            regular expression is enforced in the application on both sides of the interface.
        */
        CONSTRAINT CK_ActivationRequest_PublicId CHECK
        (
            PublicId LIKE '[A-Z]%[_][0-9]%'
            AND PublicId NOT LIKE '%[^A-Z0-9_]%'
            AND RIGHT(PublicId, 1) LIKE '[0-9]'
        ),
        CONSTRAINT CK_ActivationRequest_Status CHECK
        (
            [Status] IN
            (
                'Submitted', 'PendingCrmSync', 'AwaitingGisVerification', 'RejectedNoLine',
                'LineAvailable', 'SalesOrderOpened', 'InProvisioning', 'Closed', 'Completed',
                'IntegrationFailed'
            )
        ),
        -- TR-ACT-02 / TR-ACT-03: coordinates must be valid once normalised.
        CONSTRAINT CK_ActivationRequest_Lat CHECK (LocationLat BETWEEN -90 AND 90),
        CONSTRAINT CK_ActivationRequest_Lng CHECK (LocationLng BETWEEN -180 AND 180),
        CONSTRAINT CK_ActivationRequest_Duration CHECK (ContractDurationMonths > 0)
    );

    CREATE INDEX IX_ActivationRequest_Isp_Status     ON portal.ActivationRequest (IspId, [Status]);
    CREATE INDEX IX_ActivationRequest_CreatedAt      ON portal.ActivationRequest (CreatedAt DESC);
    -- TR-REP-05 / TR-INT-13: financial code is a filterable, indexed reconciliation key.
    CREATE INDEX IX_ActivationRequest_FinancialCode  ON portal.ActivationRequest (FinancialCode) WHERE FinancialCode IS NOT NULL;
    CREATE INDEX IX_ActivationRequest_CrmTicketId    ON portal.ActivationRequest (CrmTicketId)   WHERE CrmTicketId IS NOT NULL;
END
GO

-- --------------------------------------------------------------------------------------
-- portal.ActiveLine — TRD 3.1, TRD 6.1. Projection of the BI reference table.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ActiveLine', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ActiveLine
    (
        LineId              bigint            IDENTITY(1,1) NOT NULL,
        IspId               bigint            NOT NULL,
        ContractId          nvarchar(50)      NOT NULL,
        SubscriberReference nvarchar(100)     NOT NULL,
        Technology          nvarchar(30)      NOT NULL,
        PackageCode         nvarchar(50)      NOT NULL,
        [Status]            nvarchar(40)      NOT NULL,
        BiSyncedAt          datetimeoffset(7) NOT NULL CONSTRAINT DF_ActiveLine_BiSyncedAt DEFAULT SYSDATETIMEOFFSET(),
        BiChangeMarker      nvarchar(100)     NULL,
        CONSTRAINT PK_ActiveLine PRIMARY KEY CLUSTERED (LineId),
        -- TR-PAS-04: the upsert key that makes re-processing idempotent.
        CONSTRAINT UX_ActiveLine_Isp_ContractId UNIQUE (IspId, ContractId),
        CONSTRAINT FK_ActiveLine_Isp FOREIGN KEY (IspId) REFERENCES sec.Isp (IspId)
    );

    -- TR-PAS-05 / TR-NFR-05: server-side paged and searchable line dropdown.
    CREATE INDEX IX_ActiveLine_Isp_Technology_Status ON portal.ActiveLine (IspId, Technology, [Status]) INCLUDE (ContractId, SubscriberReference, PackageCode);
    CREATE INDEX IX_ActiveLine_SubscriberReference   ON portal.ActiveLine (SubscriberReference);
END
GO

-- --------------------------------------------------------------------------------------
-- portal.ComplaintTicket — TRD 3.1, TRD 6.2 to 6.6
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ComplaintTicket', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ComplaintTicket
    (
        TicketId           bigint            IDENTITY(1,1) NOT NULL,
        -- TR-DAT-06: distinguishable series from activation requests.
        PublicId           nvarchar(32)      NOT NULL,
        IspId              bigint            NOT NULL,
        LineId             bigint            NOT NULL,
        CategoryL1         nvarchar(50)      NOT NULL,
        CategoryL2         nvarchar(50)      NOT NULL,
        CategoryL3         nvarchar(50)      NOT NULL,
        [Description]      nvarchar(4000)    NOT NULL,
        [Status]           nvarchar(50)      NOT NULL,
        CrmTicketId        nvarchar(50)      NULL,
        ClearingCode       nvarchar(50)      NULL,
        ClearingText       nvarchar(2000)    NULL,
        ClosureDecision    nvarchar(30)      NULL,
        ClosureDecisionAt  datetimeoffset(7) NULL,
        ClosureDecisionBy  bigint            NULL,
        -- TR-PAS-21a / TR-PAS-21h: end of the Pending ISP Confirmation window.
        ConfirmationDueAt  datetimeoffset(7) NULL,
        -- TR-PAS-21f: post-closure challenge links back to the original ticket.
        ParentTicketId     bigint            NULL,
        -- TR-INT-25: occurredAt of the last applied CRM event; older events are discarded.
        LastAppliedEventAt datetimeoffset(7) NULL,
        OpenedAt           datetimeoffset(7) NOT NULL CONSTRAINT DF_ComplaintTicket_OpenedAt DEFAULT SYSDATETIMEOFFSET(),
        OpenedBy           bigint            NULL,
        ClosedAt           datetimeoffset(7) NULL,
        CONSTRAINT PK_ComplaintTicket PRIMARY KEY CLUSTERED (TicketId),
        CONSTRAINT UX_ComplaintTicket_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_ComplaintTicket_Isp    FOREIGN KEY (IspId)  REFERENCES sec.Isp (IspId),
        CONSTRAINT FK_ComplaintTicket_Line   FOREIGN KEY (LineId) REFERENCES portal.ActiveLine (LineId),
        CONSTRAINT FK_ComplaintTicket_Parent FOREIGN KEY (ParentTicketId) REFERENCES portal.ComplaintTicket (TicketId),
        CONSTRAINT CK_ComplaintTicket_PublicId CHECK
        (
            PublicId LIKE '[A-Z]%[_][0-9]%'
            AND PublicId NOT LIKE '%[^A-Z0-9_]%'
            AND RIGHT(PublicId, 1) LIKE '[0-9]'
        ),
        -- TR-PAS-21c: an auto-confirmed completion stays separable from an ISP-confirmed one.
        CONSTRAINT CK_ComplaintTicket_ClosureDecision CHECK
        (
            ClosureDecision IS NULL
            OR ClosureDecision IN ('Confirmed', 'Rejected', 'AutoConfirmed', 'CompletedByCrm')
        ),
        CONSTRAINT CK_ComplaintTicket_NoSelfParent CHECK (ParentTicketId IS NULL OR ParentTicketId <> TicketId)
    );

    CREATE INDEX IX_ComplaintTicket_CrmTicketId          ON portal.ComplaintTicket (CrmTicketId) WHERE CrmTicketId IS NOT NULL;
    -- TR-PAS-31 / TR-PAS-32: dashboard filters run on this index.
    CREATE INDEX IX_ComplaintTicket_Isp_Status_OpenedAt  ON portal.ComplaintTicket (IspId, [Status], OpenedAt DESC);
    CREATE INDEX IX_ComplaintTicket_LineId               ON portal.ComplaintTicket (LineId);
    -- TR-PAS-21h: administrator view of tickets awaiting confirmation, and the sweep query.
    CREATE INDEX IX_ComplaintTicket_ConfirmationDueAt    ON portal.ComplaintTicket (ConfirmationDueAt) WHERE ConfirmationDueAt IS NOT NULL;
END
GO

-- --------------------------------------------------------------------------------------
-- portal.TicketComment — TRD 3.1, TRD 6.6. Immutable once saved (TR-PAS-27); see 0006.
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.TicketComment', 'U') IS NULL
BEGIN
    CREATE TABLE portal.TicketComment
    (
        CommentId         bigint            IDENTITY(1,1) NOT NULL,
        TicketId          bigint            NOT NULL,
        -- NULL when the comment originated in CRM and has no portal user.
        AuthorUserId      bigint            NULL,
        AuthorType        nvarchar(20)      NOT NULL,
        AuthorDisplayName nvarchar(200)     NULL,
        Body              nvarchar(4000)    NOT NULL,
        CreatedAt         datetimeoffset(7) NOT NULL CONSTRAINT DF_TicketComment_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CrmSyncStatus     nvarchar(20)      NOT NULL,
        CrmCommentId      nvarchar(50)      NULL,
        CONSTRAINT PK_TicketComment PRIMARY KEY CLUSTERED (CommentId),
        CONSTRAINT FK_TicketComment_Ticket FOREIGN KEY (TicketId)     REFERENCES portal.ComplaintTicket (TicketId),
        CONSTRAINT FK_TicketComment_User   FOREIGN KEY (AuthorUserId) REFERENCES sec.[User] (UserId),
        CONSTRAINT CK_TicketComment_AuthorType    CHECK (AuthorType IN ('Isp', 'ServiceDesk', 'Crm')),
        CONSTRAINT CK_TicketComment_CrmSyncStatus CHECK (CrmSyncStatus IN ('Pending', 'Sent', 'Failed', 'NotApplicable'))
    );

    CREATE INDEX IX_TicketComment_Ticket_CreatedAt ON portal.TicketComment (TicketId, CreatedAt);
    -- TR-PAS-26: deduplicates comments replicated from CRM.
    CREATE UNIQUE INDEX UX_TicketComment_CrmCommentId ON portal.TicketComment (CrmCommentId) WHERE CrmCommentId IS NOT NULL;
END
GO

-- --------------------------------------------------------------------------------------
-- portal.ServiceChangeRequest — TRD 3.1, TRD 6.8
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('portal.ServiceChangeRequest', 'U') IS NULL
BEGIN
    CREATE TABLE portal.ServiceChangeRequest
    (
        ChangeId                 bigint            IDENTITY(1,1) NOT NULL,
        PublicId                 nvarchar(32)      NOT NULL,
        LineId                   bigint            NOT NULL,
        ChangeType               nvarchar(20)      NOT NULL,
        PackageAsIs              nvarchar(50)      NOT NULL,
        PackageToBe              nvarchar(50)      NULL,
        RequestedTerminationDate date              NULL,
        [Status]                 nvarchar(50)      NOT NULL,
        CrmReference             nvarchar(50)      NULL,
        CreatedAt                datetimeoffset(7) NOT NULL CONSTRAINT DF_ServiceChangeRequest_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CreatedBy                bigint            NULL,
        CONSTRAINT PK_ServiceChangeRequest PRIMARY KEY CLUSTERED (ChangeId),
        CONSTRAINT UX_ServiceChangeRequest_PublicId UNIQUE (PublicId),
        CONSTRAINT FK_ServiceChangeRequest_Line FOREIGN KEY (LineId) REFERENCES portal.ActiveLine (LineId),
        CONSTRAINT CK_ServiceChangeRequest_ChangeType CHECK (ChangeType IN ('Upgrade', 'Downgrade', 'Termination')),
        -- TR-PAS-35: a package change needs a target; TR-PAS-36: a termination needs a date.
        CONSTRAINT CK_ServiceChangeRequest_Target CHECK
        (
            (ChangeType = 'Termination' AND RequestedTerminationDate IS NOT NULL)
            OR (ChangeType <> 'Termination' AND PackageToBe IS NOT NULL AND PackageToBe <> PackageAsIs)
        )
    );

    CREATE INDEX IX_ServiceChangeRequest_Line_Status ON portal.ServiceChangeRequest (LineId, [Status]);
END
GO

/*
    0002_security_tables.sql
    TRD 3.1 entities: Role, Permission, RolePermission, ISP, User, AuditLog.
    Plus sec.UserPasswordHistory, which TRD 3.1 does not list but TR-SEC-03 requires.

    Notes
      * All timestamps are datetimeoffset(7): UTC with the offset preserved (TR-DAT-08).
      * Status columns carry the enumeration name, so the data is readable to a DBA and to BI.
      * Nothing is ever deleted; delete is blocked by trigger in 0006 (TR-DAT-07, TR-SEC-11).
      * CreatedBy / OpenedBy columns hold sec.User.UserId but carry no FK: the seeded
        administrator is created before any ISP exists, and audit-bearing columns must
        survive archival of the referenced row. The FKs named in TRD 3.1 are all present.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- --------------------------------------------------------------------------------------
-- sec.Role — TRD 3.1 "Role", roles per TRD 4.3
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.Role', 'U') IS NULL
BEGIN
    CREATE TABLE sec.Role
    (
        RoleId       bigint        IDENTITY(1,1) NOT NULL,
        Name         nvarchar(50)  NOT NULL,
        Description  nvarchar(500) NULL,
        IsSystemRole bit           NOT NULL CONSTRAINT DF_Role_IsSystemRole DEFAULT (0),
        CONSTRAINT PK_Role PRIMARY KEY CLUSTERED (RoleId),
        CONSTRAINT UX_Role_Name UNIQUE (Name)
    );
END
GO

-- --------------------------------------------------------------------------------------
-- sec.Permission — TRD 3.1 "Permission"; granular action codes evaluated on every request
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.Permission', 'U') IS NULL
BEGIN
    CREATE TABLE sec.Permission
    (
        PermissionId bigint        IDENTITY(1,1) NOT NULL,
        Code         nvarchar(100) NOT NULL,
        Description  nvarchar(500) NULL,
        CONSTRAINT PK_Permission PRIMARY KEY CLUSTERED (PermissionId),
        CONSTRAINT UX_Permission_Code UNIQUE (Code)
    );
END
GO

-- --------------------------------------------------------------------------------------
-- sec.RolePermission — TRD 3.1; administrator-configurable without a release (TR-SEC-21)
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.RolePermission', 'U') IS NULL
BEGIN
    CREATE TABLE sec.RolePermission
    (
        RoleId       bigint            NOT NULL,
        PermissionId bigint            NOT NULL,
        GrantedAt    datetimeoffset(7) NOT NULL CONSTRAINT DF_RolePermission_GrantedAt DEFAULT SYSDATETIMEOFFSET(),
        GrantedBy    bigint            NULL,
        CONSTRAINT PK_RolePermission PRIMARY KEY CLUSTERED (RoleId, PermissionId),
        CONSTRAINT FK_RolePermission_Role FOREIGN KEY (RoleId)
            REFERENCES sec.Role (RoleId),
        CONSTRAINT FK_RolePermission_Permission FOREIGN KEY (PermissionId)
            REFERENCES sec.Permission (PermissionId)
    );
END
GO

-- --------------------------------------------------------------------------------------
-- sec.Isp — TRD 3.1 "ISP"
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.Isp', 'U') IS NULL
BEGIN
    CREATE TABLE sec.Isp
    (
        IspId           bigint            IDENTITY(1,1) NOT NULL,
        Name            nvarchar(200)     NOT NULL,
        Nipt            nvarchar(20)      NOT NULL,
        ContactPerson   nvarchar(200)     NOT NULL,
        ContactEmail    nvarchar(256)     NOT NULL,
        ContactMobile   nvarchar(20)      NOT NULL,
        CrmBpReference  nvarchar(50)      NOT NULL,
        Status          nvarchar(20)      NOT NULL CONSTRAINT DF_Isp_Status DEFAULT ('Active'),
        CreatedAt       datetimeoffset(7) NOT NULL CONSTRAINT DF_Isp_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CreatedBy       bigint            NULL,
        CONSTRAINT PK_Isp PRIMARY KEY CLUSTERED (IspId),
        -- TR-SEC-15 / TR-SEC-16: NIPT unique across the platform.
        CONSTRAINT UX_Isp_Nipt UNIQUE (Nipt),
        CONSTRAINT CK_Isp_Status CHECK (Status IN ('Active', 'Locked')),
        -- TR-SEC-14 / TR-SEC-15: E.164 — leading '+' then 1..15 digits.
        CONSTRAINT CK_Isp_ContactMobile CHECK (ContactMobile LIKE '+[0-9]%' AND ContactMobile NOT LIKE '%[^+0-9]%'),
        CONSTRAINT CK_Isp_ContactEmail CHECK (ContactEmail LIKE '%_@_%._%')
    );

    CREATE INDEX IX_Isp_CrmBpReference ON sec.Isp (CrmBpReference);
END
GO

-- --------------------------------------------------------------------------------------
-- sec.User — TRD 3.1 "User"
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.[User]', 'U') IS NULL
BEGIN
    CREATE TABLE sec.[User]
    (
        UserId                bigint            IDENTITY(1,1) NOT NULL,
        -- NULL for internal users: Wholesale administrators, Service Desk, Auditor.
        IspId                 bigint            NULL,
        FullName              nvarchar(200)     NOT NULL,
        Email                 nvarchar(256)     NOT NULL,
        Mobile                nvarchar(20)      NOT NULL,
        RoleId                bigint            NOT NULL,
        Status                nvarchar(20)      NOT NULL CONSTRAINT DF_User_Status DEFAULT ('Active'),
        LastLoginAt           datetimeoffset(7) NULL,
        FailedLoginCount      int               NOT NULL CONSTRAINT DF_User_FailedLoginCount DEFAULT (0),
        -- TR-SEC-02: salted adaptive one-way hash. Reversible storage is prohibited.
        PasswordHash          nvarchar(512)     NOT NULL,
        PasswordHashAlgorithm nvarchar(50)      NOT NULL,
        PasswordUpdatedAt     datetimeoffset(7) NULL,
        -- TR-SEC-04: encrypted TOTP seed; null when the configured channel is SMS or email OTP.
        TotpSecret            varbinary(256)    NULL,
        CreatedAt             datetimeoffset(7) NOT NULL CONSTRAINT DF_User_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CreatedBy             bigint            NULL,
        CONSTRAINT PK_User PRIMARY KEY CLUSTERED (UserId),
        -- TR-SEC-01 / TR-SEC-14: unique across the entire platform.
        CONSTRAINT UX_User_Email UNIQUE (Email),
        CONSTRAINT FK_User_Isp FOREIGN KEY (IspId) REFERENCES sec.Isp (IspId),
        CONSTRAINT FK_User_Role FOREIGN KEY (RoleId) REFERENCES sec.Role (RoleId),
        CONSTRAINT CK_User_Status CHECK (Status IN ('Active', 'Locked')),
        CONSTRAINT CK_User_Mobile CHECK (Mobile LIKE '+[0-9]%' AND Mobile NOT LIKE '%[^+0-9]%'),
        CONSTRAINT CK_User_Email CHECK (Email LIKE '%_@_%._%'),
        CONSTRAINT CK_User_FailedLoginCount CHECK (FailedLoginCount >= 0)
    );

    CREATE INDEX IX_User_IspId ON sec.[User] (IspId) WHERE IspId IS NOT NULL;
END
GO

-- --------------------------------------------------------------------------------------
-- sec.UserPasswordHistory — TR-SEC-03, no reuse of the last 5 passwords
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.UserPasswordHistory', 'U') IS NULL
BEGIN
    CREATE TABLE sec.UserPasswordHistory
    (
        PasswordHistoryId     bigint            IDENTITY(1,1) NOT NULL,
        UserId                bigint            NOT NULL,
        PasswordHash          nvarchar(512)     NOT NULL,
        PasswordHashAlgorithm nvarchar(50)      NOT NULL,
        CreatedAt             datetimeoffset(7) NOT NULL CONSTRAINT DF_UserPasswordHistory_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        CONSTRAINT PK_UserPasswordHistory PRIMARY KEY CLUSTERED (PasswordHistoryId),
        CONSTRAINT FK_UserPasswordHistory_User FOREIGN KEY (UserId) REFERENCES sec.[User] (UserId)
    );

    CREATE INDEX IX_UserPasswordHistory_UserId_CreatedAt
        ON sec.UserPasswordHistory (UserId, CreatedAt DESC);
END
GO

-- --------------------------------------------------------------------------------------
-- sec.AuditLog — TRD 3.1 "AuditLog", TRD 4.4. Append-only; see 0006 for the guard trigger.
-- No FK to sec.[User]: system actions have no actor, and the log must survive archival of
-- any row it references (TR-NFR-22).
-- --------------------------------------------------------------------------------------
IF OBJECT_ID('sec.AuditLog', 'U') IS NULL
BEGIN
    CREATE TABLE sec.AuditLog
    (
        AuditId       bigint            IDENTITY(1,1) NOT NULL,
        [Timestamp]   datetimeoffset(7) NOT NULL CONSTRAINT DF_AuditLog_Timestamp DEFAULT SYSDATETIMEOFFSET(),
        ActorUserId   bigint            NULL,
        ActorIp       nvarchar(64)      NULL,
        ActionCode    nvarchar(100)     NOT NULL,
        EntityType    nvarchar(100)     NOT NULL,
        EntityId      nvarchar(64)      NULL,
        OldValue      nvarchar(max)     NULL,
        NewValue      nvarchar(max)     NULL,
        CorrelationId nvarchar(64)      NOT NULL,
        CONSTRAINT PK_AuditLog PRIMARY KEY CLUSTERED (AuditId)
    );

    -- TR-SEC-25: searchable and exportable by date, actor, ISP and action type.
    CREATE INDEX IX_AuditLog_Timestamp        ON sec.AuditLog ([Timestamp] DESC);
    CREATE INDEX IX_AuditLog_Actor_Timestamp  ON sec.AuditLog (ActorUserId, [Timestamp] DESC);
    CREATE INDEX IX_AuditLog_Entity           ON sec.AuditLog (EntityType, EntityId);
    CREATE INDEX IX_AuditLog_CorrelationId    ON sec.AuditLog (CorrelationId);
END
GO

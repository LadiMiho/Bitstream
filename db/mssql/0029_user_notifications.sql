/*
    0029_user_notifications.sql
    In-app notifications (the header bell and the Notifications page):
      * portal.UserNotification: one row per recipient each time an activation request or a
        post-activation request changes status; ReadAt is set when the user opens it.
      * portal.NotificationRule: which status changes (EventCode = '{EntityType}:{Status}') notify
        which audience: IspUsers (the active users of the request's ISP) or ServiceDesk (the active
        users with the ServiceDesk role). Seeded with sensible defaults; an Administrator changes
        them on Access Management > Notification settings.
      * notification.manage: permission for that screen, granted to Administrator. Permissions are
        read into the sign-in cookie at login, so an Administrator already signed in must sign out
        and back in to see it.
    Safe to re-run: the defaults are only inserted for switches that do not exist yet, so a re-run
    never undoes a choice made on the screen. Schema version 22.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.UserNotification', 'U') IS NULL
BEGIN
    CREATE TABLE portal.UserNotification
    (
        NotificationId  bigint IDENTITY(1, 1) NOT NULL,
        UserId          bigint             NOT NULL,
        EntityType      nvarchar(40)       NOT NULL,
        EntityPublicId  nvarchar(30)       NOT NULL,
        Status          nvarchar(40)       NOT NULL,
        PreviousStatus  nvarchar(40)       NULL,
        Detail          nvarchar(1000)     NULL,
        CreatedAt       datetimeoffset(7)  NOT NULL,
        ReadAt          datetimeoffset(7)  NULL,
        CONSTRAINT PK_UserNotification PRIMARY KEY CLUSTERED (NotificationId),
        CONSTRAINT FK_UserNotification_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );

    CREATE INDEX IX_UserNotification_User_Created ON portal.UserNotification (UserId, CreatedAt DESC);
    CREATE INDEX IX_UserNotification_User_Read ON portal.UserNotification (UserId, ReadAt);
END;
GO

IF OBJECT_ID('portal.NotificationRule', 'U') IS NULL
BEGIN
    CREATE TABLE portal.NotificationRule
    (
        EventCode   nvarchar(80)       NOT NULL,
        Audience    nvarchar(20)       NOT NULL,
        IsEnabled   bit                NOT NULL,
        UpdatedAt   datetimeoffset(7)  NULL,
        UpdatedBy   bigint             NULL,
        CONSTRAINT PK_NotificationRule PRIMARY KEY CLUSTERED (EventCode, Audience),
        CONSTRAINT CK_NotificationRule_Audience CHECK (Audience IN (N'IspUsers', N'ServiceDesk'))
    );
END;
GO

-- Defaults: ISP users hear about every step that matters to them; the service desk about the
-- requests waiting for its decision.
INSERT INTO portal.NotificationRule (EventCode, Audience, IsEnabled)
SELECT d.EventCode, d.Audience, 1
FROM (VALUES
    (N'ActivationRequest:LineAvailable',                N'IspUsers'),
    (N'ActivationRequest:RejectedNoLine',               N'IspUsers'),
    (N'ActivationRequest:SalesOrderOpened',             N'IspUsers'),
    (N'ActivationRequest:AwaitingOperatorConfirmation', N'IspUsers'),
    (N'ActivationRequest:Completed',                    N'IspUsers'),
    (N'ActivationRequest:ActivationFailed',             N'IspUsers'),
    (N'ActivationRequest:Closed',                       N'IspUsers'),
    (N'ActivationRequest:IntegrationFailed',            N'IspUsers'),
    (N'ActivationRequest:WaitingForServiceDesk',        N'ServiceDesk'),
    (N'PostActivationRequest:InProcess',                N'IspUsers'),
    (N'PostActivationRequest:TechnicallyCompleted',     N'IspUsers'),
    (N'PostActivationRequest:Failed',                   N'IspUsers')
) AS d (EventCode, Audience)
WHERE NOT EXISTS
(
    SELECT 1 FROM portal.NotificationRule r WHERE r.EventCode = d.EventCode AND r.Audience = d.Audience
);
GO

MERGE sec.Permission AS target
USING (VALUES (N'notification.manage', N'Choose which request status changes notify ISP users and service desk users')) AS source (Code, Description)
    ON target.Code = source.Code
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Description) VALUES (source.Code, source.Description);
GO

INSERT INTO sec.RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermissionId
FROM dbo.Roles r
INNER JOIN sec.Permission p ON p.Code = N'notification.manage'
WHERE r.Name = N'Administrator'
  AND NOT EXISTS
  (
      SELECT 1 FROM sec.RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermissionId
  );
GO

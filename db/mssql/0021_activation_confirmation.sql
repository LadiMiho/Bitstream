/*
    0021_activation_confirmation.sql
    End of the activation flow (after the sales order):

      SalesOrderOpened ("Activation in progress")
        -> LINE_ACTIVATED from CRM -> AwaitingOperatorConfirmation
        -> operator Yes -> Completed
        -> operator No (comment required) -> WaitingForServiceDesk
             -> service desk Success -> Completed
             -> service desk Fail    -> ActivationFailed (final)

    - CK_ActivationRequest_Status gains the three new statuses.
    - portal.ActivationRequest gains the operator and service desk decision columns.
    - Permissions:
        activation.confirm            -> IspUser, Administrator
        activation.servicedesk.decide -> ServiceDesk, Administrator
        activation.read.all           -> ServiceDesk (to see the requests waiting for them)

    Permissions are read into the sign-in cookie at login: users must sign out and back in.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.CK_ActivationRequest_Status', 'C') IS NOT NULL
    ALTER TABLE portal.ActivationRequest DROP CONSTRAINT CK_ActivationRequest_Status;
GO

ALTER TABLE portal.ActivationRequest WITH CHECK ADD CONSTRAINT CK_ActivationRequest_Status CHECK
(
    [Status] IN
    (
        'Submitted', 'PendingCrmSync', 'AwaitingGisVerification', 'RejectedNoLine',
        'LineAvailable', 'SalesOrderOpened', 'InProvisioning', 'Closed', 'Completed',
        'IntegrationFailed', 'AwaitingOperatorConfirmation', 'WaitingForServiceDesk', 'ActivationFailed'
    )
);
GO

IF COL_LENGTH('portal.ActivationRequest', 'LineActivatedAt') IS NULL
    ALTER TABLE portal.ActivationRequest ADD LineActivatedAt datetimeoffset(7) NULL;
IF COL_LENGTH('portal.ActivationRequest', 'OperatorConfirmed') IS NULL
    ALTER TABLE portal.ActivationRequest ADD OperatorConfirmed bit NULL;
IF COL_LENGTH('portal.ActivationRequest', 'OperatorComment') IS NULL
    ALTER TABLE portal.ActivationRequest ADD OperatorComment nvarchar(2000) NULL;
IF COL_LENGTH('portal.ActivationRequest', 'OperatorDecidedAt') IS NULL
    ALTER TABLE portal.ActivationRequest ADD OperatorDecidedAt datetimeoffset(7) NULL;
IF COL_LENGTH('portal.ActivationRequest', 'OperatorDecidedBy') IS NULL
    ALTER TABLE portal.ActivationRequest ADD OperatorDecidedBy bigint NULL;
IF COL_LENGTH('portal.ActivationRequest', 'ServiceDeskSucceeded') IS NULL
    ALTER TABLE portal.ActivationRequest ADD ServiceDeskSucceeded bit NULL;
IF COL_LENGTH('portal.ActivationRequest', 'ServiceDeskComment') IS NULL
    ALTER TABLE portal.ActivationRequest ADD ServiceDeskComment nvarchar(2000) NULL;
IF COL_LENGTH('portal.ActivationRequest', 'ServiceDeskDecidedAt') IS NULL
    ALTER TABLE portal.ActivationRequest ADD ServiceDeskDecidedAt datetimeoffset(7) NULL;
IF COL_LENGTH('portal.ActivationRequest', 'ServiceDeskDecidedBy') IS NULL
    ALTER TABLE portal.ActivationRequest ADD ServiceDeskDecidedBy bigint NULL;
GO

MERGE sec.Permission AS target
USING (VALUES
    (N'activation.confirm', N'Confirm whether an activated line works (operator confirmation)'),
    (N'activation.servicedesk.decide', N'Record the service desk''s final activation outcome')) AS source (Code, Description)
    ON target.Code = source.Code
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Description) VALUES (source.Code, source.Description);
GO

INSERT INTO sec.RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermissionId
FROM
(
    VALUES
        (N'IspUser',       N'activation.confirm'),
        (N'Administrator', N'activation.confirm'),
        (N'ServiceDesk',   N'activation.servicedesk.decide'),
        (N'Administrator', N'activation.servicedesk.decide'),
        (N'ServiceDesk',   N'activation.read.all')
) AS g (RoleName, PermissionCode)
INNER JOIN dbo.Roles r ON r.Name = g.RoleName
INNER JOIN sec.Permission p ON p.Code = g.PermissionCode
WHERE NOT EXISTS
(
    SELECT 1 FROM sec.RolePermission rp
    WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermissionId
);
GO

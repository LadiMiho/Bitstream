/*
    0015_seed_role_baseline.sql
    Seeded roles (TRD 4.3) and their baseline permission assignment.

    Runs after 0014_drop_legacy_identity_tables.sql: roles now live in dbo.Roles (ASP.NET
    Core Identity's own EF-migrated schema), which only exists once the identity EF migration has
    run (DevelopmentBootstrapper.cs / the documented `dotnet ef database update` step), and
    sec.RolePermission.RoleId is only re-pointed at it once 0014 has run. NormalizedName is
    required by RoleManager<Role>.FindByNameAsync (used by
    AdministrationService.ResolveRoleAsync); ConcurrencyStamp is required by Identity's own
    concurrency check on RoleManager.UpdateAsync.

    Re-running this script restores the baseline without removing an administrator's later
    grants (TR-SEC-21). Service Desk deliberately receives read plus comment only (TR-SEC-20).
    ISP User receives only "own" scoped permissions; ownership itself is enforced server-side on
    every call (TR-SEC-17 to TR-SEC-19), not by the permission code alone.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

MERGE dbo.Roles AS target
USING
(
    VALUES
        (N'Administrator', N'My Company Wholesale Team. Full management of ISPs, users and roles; GIS verification outcome; sales order confirmation; all tickets; reporting; audit log access.'),
        (N'IspUser',       N'ISP user. Scoped to the own ISP: activation requests, complaint tickets, comments, closure decisions, own reports.'),
        (N'ServiceDesk',   N'View and comment in the ticket section across all ISPs. No creation, no ticket state change, no user administration.'),
        (N'Auditor',       N'Read-only access to audit logs and reports across all ISPs.')
) AS source (Name, Description)
    ON target.Name = source.Name
WHEN MATCHED THEN
    UPDATE SET Description = source.Description, IsSystemRole = 1
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Name, NormalizedName, Description, IsSystemRole, ConcurrencyStamp)
    VALUES (source.Name, UPPER(source.Name), source.Description, 1, CONVERT(nvarchar(36), NEWID()));
GO

;WITH baseline (RoleName, PermissionCode) AS
(
    SELECT r.Name, p.Code
    FROM dbo.Roles r
    CROSS JOIN sec.Permission p
    WHERE r.Name = N'Administrator'

    UNION ALL SELECT N'IspUser', v.Code
    FROM (VALUES
        (N'activation.create'), (N'activation.read.own'),
        (N'ticket.create'), (N'ticket.read.own'), (N'ticket.comment.create'), (N'ticket.closure.decide'),
        (N'servicechange.create'), (N'servicechange.read.own'),
        (N'report.export.own')) AS v (Code)

    UNION ALL SELECT N'ServiceDesk', v.Code
    FROM (VALUES
        (N'ticket.read.all'), (N'ticket.comment.create'), (N'ticket.routing.read')) AS v (Code)

    UNION ALL SELECT N'Auditor', v.Code
    FROM (VALUES
        (N'audit.read'), (N'report.export.all'),
        (N'activation.read.all'), (N'ticket.read.all')) AS v (Code)
)
INSERT INTO sec.RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermissionId
FROM baseline b
INNER JOIN dbo.Roles r ON r.Name = b.RoleName
INNER JOIN sec.Permission p  ON p.Code = b.PermissionCode
WHERE NOT EXISTS
(
    SELECT 1 FROM sec.RolePermission rp
    WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermissionId
);
GO

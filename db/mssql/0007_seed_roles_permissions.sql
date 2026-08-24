/*
    0007_seed_roles_permissions.sql
    Seeded permission codes for TRD 4.3.

    The mapping is seeded, not hard-coded in the application: TR-SEC-21 requires the
    administrator to change role/permission assignment without a code deployment. Re-running
    this script restores the baseline without removing an administrator's later grants.

    Role seeding itself, and the baseline role/permission assignment, live in
    0015_seed_role_baseline.sql — after 0014_drop_legacy_identity_tables.sql has re-pointed
    sec.RolePermission at dbo.AspNetRoles, not here, even though this script otherwise keeps the
    original "roles and permissions" name. sec.Permission has no such dependency, so it stays
    seeded at this point in the sequence.

    No user is seeded. The first Administrator account is created by the environment
    provisioning step with a credential taken from the secret store (TR-SEC-28); seeding a
    user here would put a password hash in source control.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

MERGE sec.Permission AS target
USING
(
    VALUES
        -- Administration (TR-SEC-09 to TR-SEC-16)
        (N'isp.create',                N'Create an ISP record'),
        (N'isp.update',                N'Modify an ISP record'),
        (N'isp.lock',                  N'Lock or unlock an ISP and, with it, all of its users'),
        (N'isp.read.all',              N'Read any ISP'),
        (N'user.create',               N'Create a portal user'),
        (N'user.update',               N'Modify a portal user'),
        (N'user.lock',                 N'Lock or unlock a portal user'),
        (N'role.manage',               N'Assign permissions to roles'),
        -- Activation requests (TRD 5)
        (N'activation.create',         N'Submit an activation request'),
        (N'activation.read.own',       N'Read activation requests of the own ISP'),
        (N'activation.read.all',       N'Read activation requests of any ISP'),
        (N'activation.gis.record',     N'Record the manual GIS verification outcome'),
        -- Complaint tickets (TRD 6)
        (N'ticket.create',             N'Create a complaint ticket'),
        (N'ticket.read.own',           N'Read complaint tickets of the own ISP'),
        (N'ticket.read.all',           N'Read complaint tickets of any ISP'),
        (N'ticket.comment.create',     N'Add a comment to an open ticket'),
        (N'ticket.closure.decide',     N'Confirm or reject a proposed closure'),
        (N'ticket.routing.read',       N'See the internal routing history hidden from ISP users'),
        -- Service changes (TRD 6.8)
        (N'servicechange.create',      N'Request an upgrade, downgrade or termination'),
        (N'servicechange.read.own',    N'Read service change requests of the own ISP'),
        -- Reporting (TRD 9)
        (N'report.export.own',         N'Export reports scoped to the own ISP'),
        (N'report.export.all',         N'Export reports across all ISPs'),
        -- Audit and operations (TRD 4.4, 7.2)
        (N'audit.read',                N'Search and export the audit log'),
        (N'integration.deadletter.read', N'Inspect dead-lettered integration messages'),
        (N'integration.deadletter.replay', N'Replay a dead-lettered integration message'),
        (N'integration.sync.trigger',  N'Trigger the BI active-lines synchronisation manually')
) AS source (Code, Description)
    ON target.Code = source.Code
WHEN MATCHED THEN
    UPDATE SET Description = source.Description
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Description) VALUES (source.Code, source.Description);
GO

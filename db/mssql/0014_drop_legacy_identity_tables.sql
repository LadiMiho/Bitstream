/*
    0014_drop_legacy_identity_tables.sql

    Identity now runs on ASP.NET Core Identity's own EF-migrated schema
    (Bitstream.Infrastructure.Persistence/Identity/BitstreamIdentityDbContext.cs) — dbo.Users
    and dbo.Roles replace sec.[User] and sec.Role. This is the one deliberate exception to
    ADR-0002 ("no EF migrations, ever"), narrowed to the identity subsystem only.

    This script must run AFTER the EF migration has created dbo.Users/Roles
    (DevelopmentBootstrapper.cs runs BitstreamIdentityDbContext.Database.MigrateAsync() before
    applying db/mssql; the equivalent manual step is documented for a real deployment) AND after
    every script that still creates/alters sec.[User] or sec.Role (0002, 0003, 0006, 0009, 0012,
    0013) — hence staying last in the numbering rather than moving earlier. Role/RolePermission
    seeding against the new dbo.Roles table is correspondingly in 0015_seed_role_baseline.sql,
    which runs after this one, not in 0007 (which seeds only sec.Permission — the table this
    script does not touch).

    Dev-only reset: sec.[User]/sec.Role are dropped, not migrated — there is no production data
    to preserve. Every FK that pointed at them is re-pointed at the new tables; nothing else about
    those tables (sec.RolePermission, sec.UserPasswordHistory, portal.TicketComment) changes.
    sec.UserSession/sec.TwoFactorChallenge, which also pointed at sec.[User], are dropped outright
    (not re-pointed) by 0016_drop_session_and_twofactor_tables.sql — both fully superseded by
    ASP.NET Core Identity's own cookie authentication and 2FA token providers.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- Step 1: drop the FKs that point at sec.[User]/sec.Role, so those tables can be dropped.
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_RolePermission_Role')
    ALTER TABLE sec.RolePermission DROP CONSTRAINT FK_RolePermission_Role;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_UserPasswordHistory_User')
    ALTER TABLE sec.UserPasswordHistory DROP CONSTRAINT FK_UserPasswordHistory_User;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TicketComment_User')
    ALTER TABLE portal.TicketComment DROP CONSTRAINT FK_TicketComment_User;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_UserSession_User')
    ALTER TABLE sec.UserSession DROP CONSTRAINT FK_UserSession_User;
GO

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TwoFactorChallenge_User')
    ALTER TABLE sec.TwoFactorChallenge DROP CONSTRAINT FK_TwoFactorChallenge_User;
GO

-- Step 2: drop the legacy tables themselves (their own FKs to each other, FK_User_Isp and
-- FK_User_Role, go with them).
IF OBJECT_ID('sec.[User]', 'U') IS NOT NULL
    DROP TABLE sec.[User];
GO

IF OBJECT_ID('sec.Role', 'U') IS NOT NULL
    DROP TABLE sec.Role;
GO

-- Step 3: re-point every FK dropped in step 1 at the new EF-migrated tables, except
-- sec.UserSession/sec.TwoFactorChallenge — those tables are dropped outright by 0016, not
-- re-pointed, since they have no reason to keep existing once Identity owns sessions and 2FA.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_RolePermission_Role')
    ALTER TABLE sec.RolePermission
        ADD CONSTRAINT FK_RolePermission_Role FOREIGN KEY (RoleId) REFERENCES dbo.Roles (Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_UserPasswordHistory_User')
    ALTER TABLE sec.UserPasswordHistory
        ADD CONSTRAINT FK_UserPasswordHistory_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_TicketComment_User')
    ALTER TABLE portal.TicketComment
        ADD CONSTRAINT FK_TicketComment_User FOREIGN KEY (AuthorUserId) REFERENCES dbo.Users (Id);
GO

/*
    0032_identity_permissions.sql
    Grants the application service account what it needs on the objects 0008_permissions.sql
    does not cover, so the portal works under that least-privileged account:

      * the ASP.NET Core Identity tables in the dbo schema, created by the EF Core migration
        (BitstreamIdentityDbContext) after 0008 was written. Without these grants no one can
        sign in: reading dbo.Users is refused;
      * ops.usp_NextPrefixedIdentifier (0018, 0025), which numbers activation requests per ISP
        ticket code (TRING001). Without it no activation request can be submitted.

    Still no DDL and nothing beyond what the application does:
      * dbo.Users: read, create, update (users are never deleted; they are locked);
      * dbo.Roles, dbo.RoleClaims: read only (roles are seeded by 0015, never edited by the app);
      * dbo.UserRoles, dbo.UserClaims, dbo.UserLogins, dbo.UserTokens: read and write including
        DELETE, which ASP.NET Core Identity itself issues, e.g. when an authenticator key is reset.

    Set $(AppUser) as for 0008: Deploy-Database.ps1 -AppUser 'CORP\svc_bitstream_prod'.
    Safe to re-run. Permissions only; the schema version is unchanged (23).
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF '$(AppUser)' = '' OR '$(AppUser)' LIKE '$%'
BEGIN
    RAISERROR('Set the AppUser variable, e.g. sqlcmd -v AppUser="DOMAIN\svc_account".', 16, 1);
    SET NOEXEC ON;
END
GO

GRANT SELECT, INSERT, UPDATE         ON dbo.Users      TO [$(AppUser)];
GRANT SELECT                         ON dbo.Roles      TO [$(AppUser)];
GRANT SELECT                         ON dbo.RoleClaims TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UserRoles  TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UserClaims TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UserLogins TO [$(AppUser)];
GRANT SELECT, INSERT, UPDATE, DELETE ON dbo.UserTokens TO [$(AppUser)];

GRANT EXECUTE ON ops.usp_NextPrefixedIdentifier TO [$(AppUser)];
GO

SET NOEXEC OFF;
GO

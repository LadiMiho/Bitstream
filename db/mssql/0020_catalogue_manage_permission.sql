/*
    0020_catalogue_manage_permission.sql
    catalogue.manage: maintain the package + contract duration codes (portal.PackageOffer) from the
    Package offers screen (/ActivationRequests/PackageOffers). Granted to Administrator only.

    Permissions are read into the sign-in cookie at login, so an Administrator who is already
    signed in must sign out and back in to see the screen.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

MERGE sec.Permission AS target
USING (VALUES (N'catalogue.manage', N'Maintain package offers (package + contract duration codes)')) AS source (Code, Description)
    ON target.Code = source.Code
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Code, Description) VALUES (source.Code, source.Description);
GO

INSERT INTO sec.RolePermission (RoleId, PermissionId)
SELECT r.Id, p.PermissionId
FROM dbo.Roles r
INNER JOIN sec.Permission p ON p.Code = N'catalogue.manage'
WHERE r.Name = N'Administrator'
  AND NOT EXISTS
  (
      SELECT 1 FROM sec.RolePermission rp
      WHERE rp.RoleId = r.Id AND rp.PermissionId = p.PermissionId
  );
GO

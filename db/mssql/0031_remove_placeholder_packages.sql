/*
    0031_remove_placeholder_packages.sql
    Earlier versions of 0017_activation_catalogues.sql seeded three placeholder packages
    (BITSTREAM_BASIC, BITSTREAM_STD, BITSTREAM_PRO) and, because every deployment re-runs every
    script, put them back on each publish. 0017 no longer seeds packages; this script removes the
    placeholders that are already in the database, together with any package offers made for them.

    A placeholder that an activation request or an active line refers to is kept, with its offers,
    so the history of that request or line still shows its package. Run
    db/maintenance/clear-test-data.sql to remove test requests first; the placeholders then go on
    the next deployment. A kept placeholder can also be deactivated on Activation Requests >
    Packages & durations. Safe to re-run. Data only; the schema version is unchanged (23).
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @Placeholders TABLE (Code nvarchar(50) PRIMARY KEY);

INSERT INTO @Placeholders (Code)
SELECT p.Code
FROM portal.Package AS p
WHERE p.Code IN (N'BITSTREAM_BASIC', N'BITSTREAM_STD', N'BITSTREAM_PRO')
  AND NOT EXISTS (SELECT 1 FROM portal.ActivationRequest AS r WHERE r.PackageCode = p.Code)
  AND NOT EXISTS (SELECT 1 FROM portal.ActiveLine AS l WHERE l.PackageCode = p.Code);

DELETE o
FROM portal.PackageOffer AS o
WHERE o.PackageCode IN (SELECT Code FROM @Placeholders);

DELETE p
FROM portal.Package AS p
WHERE p.Code IN (SELECT Code FROM @Placeholders);
GO

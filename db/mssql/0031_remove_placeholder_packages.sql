/*
    0031_remove_placeholder_packages.sql
    Earlier versions of 0017_activation_catalogues.sql seeded three placeholder packages
    (BITSTREAM_BASIC, BITSTREAM_STD, BITSTREAM_PRO) and, because every deployment re-runs every
    script, put them back on each publish. 0017 no longer seeds packages; this script removes the
    placeholders that are already in the database.

    A placeholder is removed only while nothing uses it — no activation request, active line or
    package offer refers to its code — so a package that has been put to real use is kept (it can
    still be deactivated on Activation Requests > Packages & durations). Safe to re-run. Data only;
    the schema version is unchanged (23).
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DELETE p
FROM portal.Package AS p
WHERE p.Code IN (N'BITSTREAM_BASIC', N'BITSTREAM_STD', N'BITSTREAM_PRO')
  AND NOT EXISTS (SELECT 1 FROM portal.ActivationRequest AS r WHERE r.PackageCode = p.Code)
  AND NOT EXISTS (SELECT 1 FROM portal.ActiveLine AS l WHERE l.PackageCode = p.Code)
  AND NOT EXISTS (SELECT 1 FROM portal.PackageOffer AS o WHERE o.PackageCode = p.Code);
GO

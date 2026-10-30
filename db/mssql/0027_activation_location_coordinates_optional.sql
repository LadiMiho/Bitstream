/*
    0027_activation_location_coordinates_optional.sql
    The location of an activation request may now be a Google Maps short link
    (https://maps.app.goo.gl/...), which carries no coordinates, so ActivationRequest.LocationLat
    and LocationLng become nullable. Requests that already have coordinates keep them; the range
    check constraints (0003) still apply whenever a value is present.
    Safe to re-run. Schema version 20.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('portal.ActivationRequest') AND name = 'LocationLat' AND is_nullable = 0)
    ALTER TABLE portal.ActivationRequest ALTER COLUMN LocationLat decimal(9,6) NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('portal.ActivationRequest') AND name = 'LocationLng' AND is_nullable = 0)
    ALTER TABLE portal.ActivationRequest ALTER COLUMN LocationLng decimal(9,6) NULL;
GO

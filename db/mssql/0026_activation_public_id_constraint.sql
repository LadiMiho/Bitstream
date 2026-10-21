/*
    0026_activation_public_id_constraint.sql
    CK_ActivationRequest_PublicId (created in 0003) required an underscore, so it refused the
    activation request IDs issued since 0025 (TRING001). It is re-created to accept the format
    ^[A-Z]+_?[0-9]+$ — letters, an optional single underscore, then digits — so both TRING001 and
    the IDs issued before 0025 (TRING_001) are valid. The exact regular expression is still
    enforced by the application; this is the closest T-SQL LIKE approximation:
      * starts with a letter and ends with a digit;
      * only A-Z, 0-9 and _ ;
      * no letter or underscore after the first digit (letters first, then digits);
      * at most one underscore.
    Safe to re-run. Schema version 19.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.CK_ActivationRequest_PublicId', 'C') IS NOT NULL
    ALTER TABLE portal.ActivationRequest DROP CONSTRAINT CK_ActivationRequest_PublicId;
GO

ALTER TABLE portal.ActivationRequest WITH CHECK ADD CONSTRAINT CK_ActivationRequest_PublicId CHECK
(
    PublicId COLLATE Latin1_General_BIN LIKE '[A-Z]%[0-9]'
    AND PublicId COLLATE Latin1_General_BIN NOT LIKE '%[^A-Z0-9_]%'
    AND PublicId COLLATE Latin1_General_BIN NOT LIKE '%[0-9]%[A-Z_]%'
    AND PublicId COLLATE Latin1_General_BIN NOT LIKE '%[_]%[_]%'
);
GO

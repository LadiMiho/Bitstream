/*
    0022_data_protection_keys.sql
    ops.DataProtectionKeys: the ASP.NET Core Data Protection key ring, shared by every host and
    machine that uses this database (Bitstream.Web, Bitstream.Api, a developer's PC, the IIS
    server).

    Data Protection encrypts the TOTP authenticator keys in dbo.UserTokens and the sign-in
    cookies. With each machine keeping its own private keys, a value written on one could not be
    read on another ("The key {...} was not found in the key ring"), which broke sign-in on a
    shared database. Schema version 15.

    The ops schema grants in 0008 already cover this table.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('ops.DataProtectionKeys', 'U') IS NULL
BEGIN
    CREATE TABLE ops.DataProtectionKeys
    (
        Id           int IDENTITY(1, 1) NOT NULL,
        FriendlyName nvarchar(max)      NULL,
        Xml          nvarchar(max)      NULL,
        CONSTRAINT PK_DataProtectionKeys PRIMARY KEY CLUSTERED (Id)
    );
END;
GO

/*
    0023_support_classifications.sql
    portal.SupportClassification: the three-level classification of post-activation requests
    (complaints, contract changes, cancellations). The ISP picks level 1, then a level 2 under
    it, then a level 3 under that; the level-3 Code is what CRM receives.

    One self-referencing table for all three levels: a level-1 row has no parent, a level-2 row's
    parent is level 1, a level-3 row's parent is level 2. That cross-row rule is enforced by the
    application (SupportClassificationService); the constraints below cover what one row can say.

    Seeded with the list supplied by the business (October 2026). MERGE on Code: re-running the
    script adds missing rows and never overwrites names or order changed in the portal.
    Schema version 16. The portal schema grants in 0008 already cover this table.
*/
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('portal.SupportClassification', 'U') IS NULL
BEGIN
    CREATE TABLE portal.SupportClassification
    (
        ClassificationId bigint IDENTITY(1, 1) NOT NULL,
        Code             nvarchar(20)       NOT NULL,
        Name             nvarchar(200)      NOT NULL,
        [Level]          tinyint            NOT NULL,
        ParentId         bigint             NULL,
        DisplayOrder     int                NOT NULL CONSTRAINT DF_SupportClassification_DisplayOrder DEFAULT (0),
        IsActive         bit                NOT NULL CONSTRAINT DF_SupportClassification_IsActive DEFAULT (1),
        CreatedAt        datetimeoffset(7)  NOT NULL CONSTRAINT DF_SupportClassification_CreatedAt DEFAULT SYSDATETIMEOFFSET(),
        UpdatedAt        datetimeoffset(7)  NULL,
        CONSTRAINT PK_SupportClassification PRIMARY KEY CLUSTERED (ClassificationId),
        CONSTRAINT UX_SupportClassification_Code UNIQUE (Code),
        CONSTRAINT FK_SupportClassification_Parent FOREIGN KEY (ParentId) REFERENCES portal.SupportClassification (ClassificationId),
        CONSTRAINT CK_SupportClassification_Level CHECK ([Level] BETWEEN 1 AND 3),
        CONSTRAINT CK_SupportClassification_Parent CHECK (([Level] = 1 AND ParentId IS NULL) OR ([Level] > 1 AND ParentId IS NOT NULL)),
        CONSTRAINT CK_SupportClassification_Code CHECK (LEN(Code) > 0 AND Code NOT LIKE '%[^0-9]%')
    );

    CREATE INDEX IX_SupportClassification_Parent_Order ON portal.SupportClassification (ParentId, DisplayOrder);
END;
GO

-- Seed: (Code, Name, Level, ParentCode, DisplayOrder). Parents are listed before their children.
DECLARE @Seed TABLE (Seq int IDENTITY(1, 1), Code nvarchar(20), Name nvarchar(200), [Level] tinyint, ParentCode nvarchar(20) NULL, DisplayOrder int);
INSERT INTO @Seed (Code, Name, [Level], ParentCode, DisplayOrder) VALUES
    (N'40',         N'BITSTREAM COMPLAINTS',                       1, NULL,      10),
    (N'400144',     N'NETWORK',                                    2, N'40',     10),
    (N'4001440010', N'Cannot access to the internet',              3, N'400144', 10),
    (N'4001440011', N'Speed Problem',                              3, N'400144', 20),
    (N'4001440012', N'Video Blank in IPTV',                        3, N'400144', 30),
    (N'43',         N'BITSTREAM REQUEST',                          1, NULL,      20),
    (N'430013',     N'Contract Change - Upgrade',                  2, N'43',     10),
    (N'4300130010', N'IP - FROM  50/10  to  100/20 MBPS',          3, N'430013', 10),
    (N'4300130011', N'IP - FROM  50/10  to  300/50 MBPS',          3, N'430013', 20),
    (N'4300130012', N'IP - FROM  100/20  to  300/50 MBPS',         3, N'430013', 30),
    (N'4300130013', N'ETHERNET - FROM  50/10  to  100/20 MBPS',    3, N'430013', 40),
    (N'4300130014', N'ETHERNET - FROM  50/10  to  300/50 MBPS',    3, N'430013', 50),
    (N'4300130015', N'ETHERNET - FROM  100/20  to  300/50 MBPS',   3, N'430013', 60),
    (N'430014',     N'Contract Change - Downgrade',                2, N'43',     20),
    (N'4300140010', N'IP - FROM  300/50  to  100/20 MBPS',         3, N'430014', 10),
    (N'4300140011', N'IP - FROM  300/50  to  50/10 MBPS',          3, N'430014', 20),
    (N'4300140012', N'IP - FROM  100/20  to  50/10 MBPS',          3, N'430014', 30),
    (N'4300140013', N'ETHERNET  - FROM  300/50  to  100/20 MBPS',  3, N'430014', 40),
    (N'4300140014', N'ETHERNET  - FROM  300/50  to  50/10 MBPS',   3, N'430014', 50),
    (N'4300140015', N'ETHERNET  - FROM  100/20  to  50/10 MBPS',   3, N'430014', 60),
    (N'430015',     N'Contract CANCELLATION',                      2, N'43',     30),
    (N'4300150010', N'SERVICE NO NEEDED',                          3, N'430015', 10),
    (N'4300150011', N'INTERNET SERVICE PROBLEM',                   3, N'430015', 20),
    (N'4300150012', N'HIGH TARIFFS',                               3, N'430015', 30),
    (N'4300150013', N'DELAYS IN SOLVING PROBLEMS',                 3, N'430015', 40),
    (N'4300150014', N'OTHER',                                      3, N'430015', 50);

-- Level by level, so every child's parent exists when the child is inserted.
DECLARE @CurrentLevel tinyint = 1;
WHILE @CurrentLevel <= 3
BEGIN
    INSERT INTO portal.SupportClassification (Code, Name, [Level], ParentId, DisplayOrder)
    SELECT s.Code, s.Name, s.[Level], p.ClassificationId, s.DisplayOrder
    FROM @Seed s
    LEFT JOIN portal.SupportClassification p ON p.Code = s.ParentCode
    WHERE s.[Level] = @CurrentLevel
      AND NOT EXISTS (SELECT 1 FROM portal.SupportClassification e WHERE e.Code = s.Code);

    SET @CurrentLevel += 1;
END;
GO

USE [TEMP_API];
GO

IF OBJECT_ID(N'dbo.INT_IntercompanyTransfer', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.INT_IntercompanyTransfer
    (
        SiteId NVARCHAR(20) NOT NULL,
        TransferId NVARCHAR(80) NOT NULL,
        SourceCompanyName NVARCHAR(128) NOT NULL,
        TargetCompanyName NVARCHAR(128) NOT NULL,
        PostingDate DATE NOT NULL,

        RequestHash CHAR(64) NOT NULL,
        RequestJson NVARCHAR(MAX) NOT NULL,
        Status VARCHAR(20) NOT NULL,

        GoodsIssueDocEntry INT NULL,
        GoodsIssueDocNum INT NULL,
        GoodsReceiptDocEntry INT NULL,
        GoodsReceiptDocNum INT NULL,
        ActualCostJson NVARCHAR(MAX) NULL,

        RetryCount INT NOT NULL
            CONSTRAINT DF_INT_IntercompanyTransfer_RetryCount DEFAULT 0,
        ErrorMessage NVARCHAR(MAX) NULL,
        CreateDate DATETIME2(3) NOT NULL
            CONSTRAINT DF_INT_IntercompanyTransfer_CreateDate DEFAULT SYSDATETIME(),
        UpdateDate DATETIME2(3) NOT NULL
            CONSTRAINT DF_INT_IntercompanyTransfer_UpdateDate DEFAULT SYSDATETIME(),
        ProcessBy NVARCHAR(128) NULL,

        CONSTRAINT PK_INT_IntercompanyTransfer
            PRIMARY KEY CLUSTERED (SiteId, TransferId),
        CONSTRAINT CK_INT_IntercompanyTransfer_Status
            CHECK (Status IN ('NEW', 'ERROR', 'GI_FAILED', 'GI_POSTED', 'GR_PENDING', 'COMPLETED'))
    );
END;
GO

IF OBJECT_ID(N'dbo.INT_IntercompanyTransfer', N'U') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.check_constraints
       WHERE name = N'CK_INT_IntercompanyTransfer_Status'
         AND parent_object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
         AND definition NOT LIKE N'%''ERROR''%'
   )
BEGIN
    ALTER TABLE dbo.INT_IntercompanyTransfer
        DROP CONSTRAINT CK_INT_IntercompanyTransfer_Status;
END;
GO

IF OBJECT_ID(N'dbo.INT_IntercompanyTransfer', N'U') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.check_constraints
       WHERE name = N'CK_INT_IntercompanyTransfer_Status'
         AND parent_object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
   )
BEGIN
    ALTER TABLE dbo.INT_IntercompanyTransfer WITH CHECK
        ADD CONSTRAINT CK_INT_IntercompanyTransfer_Status
        CHECK (Status IN ('NEW', 'ERROR', 'GI_FAILED', 'GI_POSTED', 'GR_PENDING', 'COMPLETED'));

    ALTER TABLE dbo.INT_IntercompanyTransfer
        CHECK CONSTRAINT CK_INT_IntercompanyTransfer_Status;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_INT_IntercompanyTransfer_Status'
      AND object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
)
BEGIN
    CREATE INDEX IX_INT_IntercompanyTransfer_Status
    ON dbo.INT_IntercompanyTransfer(Status, UpdateDate, SiteId);
END;
GO

SELECT
    DB_NAME() AS DatabaseName,
    name AS TableName,
    create_date AS CreateDate
FROM sys.tables
WHERE name = N'INT_IntercompanyTransfer';
GO

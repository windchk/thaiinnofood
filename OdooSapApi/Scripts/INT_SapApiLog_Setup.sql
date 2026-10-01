USE [TEMP_API];
GO

IF OBJECT_ID(N'dbo.INT_SapApiLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.INT_SapApiLog
    (
        LogId INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_INT_SapApiLog PRIMARY KEY,

        SiteId NVARCHAR(10) NOT NULL,
        SapDatabaseName NVARCHAR(50) NOT NULL,

        ProcessType NVARCHAR(30) NOT NULL, -- IssueFromProduction, ReceiptFromProduction, Delivery; CloseProductionOrder is disabled
        ProductionOrderDocEntry INT NOT NULL, -- Source DocEntry: Production Order or A/R Reserve Invoice

        RequestJson NVARCHAR(MAX) NULL,
        ResponseJson NVARCHAR(MAX) NULL,

        Status CHAR(1) NOT NULL, -- P=Processing, S=Success, E=Expected/business error, X=Exception
        ErrorMessage NVARCHAR(MAX) NULL,

        SapDocumentEntry NVARCHAR(50) NULL, -- Issue/Receipt/Delivery DocEntry; legacy close value retained
        SapDocumentNumber NVARCHAR(50) NULL, -- Issue/Receipt/Delivery DocNum; legacy close value retained

        RequestDate DATETIME NOT NULL
            CONSTRAINT DF_INT_SapApiLog_RequestDate DEFAULT GETDATE(),
        CompletedDate DATETIME NULL,
        ProcessBy NVARCHAR(100) NULL
    );
END;
GO

IF COL_LENGTH(N'dbo.INT_SapApiLog', N'RequestDate') IS NULL
   AND COL_LENGTH(N'dbo.INT_SapApiLog', N'CreateDate') IS NOT NULL
BEGIN
    EXEC sys.sp_rename
        N'dbo.INT_SapApiLog.CreateDate',
        N'RequestDate',
        N'COLUMN';
END;
GO

IF COL_LENGTH(N'dbo.INT_SapApiLog', N'CompletedDate') IS NULL
   AND COL_LENGTH(N'dbo.INT_SapApiLog', N'ProcessDate') IS NOT NULL
BEGIN
    EXEC sys.sp_rename
        N'dbo.INT_SapApiLog.ProcessDate',
        N'CompletedDate',
        N'COLUMN';
END;
GO

IF OBJECT_ID(N'dbo.DF_INT_SapApiLog_CreateDate', N'D') IS NOT NULL
   AND OBJECT_ID(N'dbo.DF_INT_SapApiLog_RequestDate', N'D') IS NULL
BEGIN
    EXEC sys.sp_rename
        N'dbo.DF_INT_SapApiLog_CreateDate',
        N'DF_INT_SapApiLog_RequestDate',
        N'OBJECT';
END;
GO

IF EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_INT_SapApiLog_ProcessDate'
      AND object_id = OBJECT_ID(N'dbo.INT_SapApiLog')
)
AND NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_INT_SapApiLog_CompletedDate'
      AND object_id = OBJECT_ID(N'dbo.INT_SapApiLog')
)
BEGIN
    EXEC sys.sp_rename
        N'dbo.INT_SapApiLog.IX_INT_SapApiLog_ProcessDate',
        N'IX_INT_SapApiLog_CompletedDate',
        N'INDEX';
END;
GO

-- Two-stage logging semantics:
-- RequestDate   = request accepted by the API and the initial P row was inserted.
-- CompletedDate = final S/E/X result became known and the same row was updated.
-- CompletedDate remains NULL while the request is still processing or final log update failed.

IF COL_LENGTH(N'dbo.INT_SapApiLog', N'SapDocumentNumber') IS NULL
BEGIN
    ALTER TABLE dbo.INT_SapApiLog
    ADD SapDocumentNumber NVARCHAR(50) NULL;
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_INT_SapApiLog_CompletedDate'
      AND object_id = OBJECT_ID(N'dbo.INT_SapApiLog')
)
BEGIN
    CREATE INDEX IX_INT_SapApiLog_CompletedDate
    ON dbo.INT_SapApiLog(CompletedDate, SiteId, ProcessType, Status);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_INT_SapApiLog_ProductionOrder'
      AND object_id = OBJECT_ID(N'dbo.INT_SapApiLog')
)
BEGIN
    CREATE INDEX IX_INT_SapApiLog_ProductionOrder
    ON dbo.INT_SapApiLog(SapDatabaseName, ProductionOrderDocEntry, ProcessType, RequestDate);
END;
GO

SELECT
    DB_NAME() AS DatabaseName,
    name AS TableName,
    create_date AS TableCreateDate
FROM sys.tables
WHERE name = N'INT_SapApiLog';
GO

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

        ProcessType NVARCHAR(30) NOT NULL, -- IssueFromProduction, ReceiptFromProduction, CloseProductionOrder
        ProductionOrderDocEntry INT NOT NULL,

        RequestJson NVARCHAR(MAX) NULL,
        ResponseJson NVARCHAR(MAX) NULL,

        Status CHAR(1) NOT NULL, -- S=Success, E=Error
        ErrorMessage NVARCHAR(MAX) NULL,

        SapDocumentEntry NVARCHAR(50) NULL, -- Issue/Receipt DocEntry; ProductionOrder DocEntry for CloseProductionOrder
        SapDocumentNumber NVARCHAR(50) NULL, -- Issue/Receipt DocNum; ProductionOrder DocNum for CloseProductionOrder

        CreateDate DATETIME NOT NULL
            CONSTRAINT DF_INT_SapApiLog_CreateDate DEFAULT GETDATE(),
        ProcessDate DATETIME NULL,
        ProcessBy NVARCHAR(100) NULL
    );
END;
GO

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
    WHERE name = N'IX_INT_SapApiLog_ProcessDate'
      AND object_id = OBJECT_ID(N'dbo.INT_SapApiLog')
)
BEGIN
    CREATE INDEX IX_INT_SapApiLog_ProcessDate
    ON dbo.INT_SapApiLog(ProcessDate, SiteId, ProcessType, Status);
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
    ON dbo.INT_SapApiLog(SapDatabaseName, ProductionOrderDocEntry, ProcessType, CreateDate);
END;
GO

SELECT
    DB_NAME() AS DatabaseName,
    name AS TableName,
    create_date AS CreateDate
FROM sys.tables
WHERE name = N'INT_SapApiLog';
GO

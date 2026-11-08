-- HISTORICAL: A/R now uses DocDate. Use Apply_PRD_AR_DocDate_Cutoff_Backfill.sql.
-- Retained as the 2026-11-07 deployment record; do not apply this older rule.
-- PRD SAP -> Odoo: DueDate >= 2026-11-09, inclusive.
-- Covers ProductionOrder (TIF) and ARReserveInvoice (TIF and STL).
-- Future trigger events still include Release/Add and Cancel.
-- BACKFILL ONLY: Released production orders and open, non-cancelled reserves.
-- User explicitly excludes Planned, Closed and Cancelled documents from backfill.
-- Preview is the default. Set @Apply=1 to update triggers and insert queue rows.
-- Existing Worker sends inserted rows; it does not need deployment or restart.
-- No SAP document rows or existing queue rows are modified, reset or deleted.
-- Backfill skips confirmed Odoo successes and pending work across legacy/canonical sites.
-- Ambiguous previous attempts stop apply instead of silently replaying them.
-- All trigger updates and queue inserts commit together; states are preserved.
-- Baseline trigger bodies captured from TIFSVR01-SAP on 2026-11-07.

USE [TEMP_API];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;

DECLARE @Apply BIT = 0;
DECLARE @MaximumBackfillRows INT = 2000;
DECLARE @BatchStarted DATETIME = GETDATE();
DECLARE @QueueRowsLocked BIGINT;
DECLARE @AppLockResult INT;

IF CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ServerName')) <> N'TIFSVR01-SAP'
    THROW 51200, 'Wrong SQL Server. Expected TIFSVR01-SAP.', 1;

IF DB_ID(N'TIF_GOLIVE') IS NULL OR DB_ID(N'SBO_STL_GOLIVE') IS NULL
    THROW 51201, 'Required PRD SAP databases were not found.', 1;

IF OBJECT_ID(N'TEMP_API.dbo.INT_OdooQueue', N'U') IS NULL
    THROW 51202, 'TEMP_API.dbo.INT_OdooQueue was not found.', 1;

IF COL_LENGTH(N'TIF_GOLIVE.dbo.OWOR', N'DueDate') IS NULL
   OR COL_LENGTH(N'TIF_GOLIVE.dbo.OINV', N'DocDueDate') IS NULL
   OR COL_LENGTH(N'SBO_STL_GOLIVE.dbo.OINV', N'DocDueDate') IS NULL
    THROW 51203, 'Required SAP due-date columns were not found.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
      AND parent_id=OBJECT_ID(N'TIF_GOLIVE.dbo.OWOR', N'U')
)
    THROW 51204, 'TIF_GOLIVE.TRG_OWOR_ToOdooQueue was not found on the expected table.', 1;

DECLARE @ProductionDefinitionWasDisabled BIT =
(
    SELECT is_disabled FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
);

DECLARE @ProductionDefinition NVARCHAR(MAX)=N'ALTER TRIGGER dbo.TRG_OWOR_ToOdooQueue
ON dbo.OWOR
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO TEMP_API.dbo.INT_OdooQueue
    (
        ObjectType,
        ObjectKey,
        ActionType,
        SiteId,
        CompanyName,
        Status,
        RetryCount,
        CreateDate
    )
    SELECT
        ''ProductionOrder'',
        CAST(I.DocEntry AS NVARCHAR(50)),
        CASE I.Status WHEN ''R'' THEN ''RELEASED'' ELSE ''CANCEL'' END,
        ''PRD'',
        ''TIF'',
        ''N'',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    WHERE I.DueDate >= CONVERT(DATE, ''20261109'', 112)
        AND
        (
            (
                I.Status = ''R''
                AND ISNULL(D.Status, '''') <> ''R''
            )
            OR
            (
                I.Status = ''C''
                AND ISNULL(D.Status, '''') <> ''C''
            )
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM TEMP_API.dbo.INT_OdooQueue Q
            WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = ''ProductionOrder''
              AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
              AND Q.ActionType COLLATE DATABASE_DEFAULT = CASE I.Status WHEN ''R'' THEN ''RELEASED'' ELSE ''CANCEL'' END
              AND Q.SiteId COLLATE DATABASE_DEFAULT = ''PRD''
              AND Q.CompanyName COLLATE DATABASE_DEFAULT = ''TIF''
              AND Q.Status COLLATE DATABASE_DEFAULT IN (''N'', ''P'')
        );
END;
';

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND parent_id=OBJECT_ID(N'TIF_GOLIVE.dbo.OINV', N'U')
)
    THROW 51205, 'TIF_GOLIVE.TRG_OINV_Reserve_ToOdooQueue was not found on the expected table.', 1;

DECLARE @TifReserveDefinitionWasDisabled BIT =
(
    SELECT is_disabled FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
);

DECLARE @TifReserveDefinition NVARCHAR(MAX)=N'ALTER TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
ON dbo.OINV
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO TEMP_API.dbo.INT_OdooQueue
    (
        ObjectType,
        ObjectKey,
        ActionType,
        SiteId,
        CompanyName,
        Status,
        RetryCount,
        CreateDate
    )
    SELECT
        ''ARReserveInvoice'',
        CAST(I.DocEntry AS NVARCHAR(50)),
        A.ActionType,
        ''PRD'',
        ''TIF'',
        ''N'',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    CROSS APPLY
    (
        SELECT CASE
            WHEN D.DocEntry IS NULL AND ISNULL(I.CANCELED, ''N'') <> ''C'' THEN ''ADD''
            WHEN I.CANCELED = ''Y'' AND ISNULL(D.CANCELED, ''N'') <> ''Y'' THEN ''CANCEL''
        END AS ActionType
    ) A
    WHERE I.isIns = ''Y''
      AND I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)
      AND A.ActionType IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM TEMP_API.dbo.INT_OdooQueue Q
          WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = ''ARReserveInvoice''
            AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
            AND Q.ActionType COLLATE DATABASE_DEFAULT = A.ActionType COLLATE DATABASE_DEFAULT
            AND Q.SiteId COLLATE DATABASE_DEFAULT = ''PRD''
            AND Q.CompanyName COLLATE DATABASE_DEFAULT = ''TIF''
            AND Q.Status COLLATE DATABASE_DEFAULT IN (''N'', ''P'')
      );
END;
';

IF NOT EXISTS
(
    SELECT 1 FROM [SBO_STL_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND parent_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.OINV', N'U')
)
    THROW 51206, 'SBO_STL_GOLIVE.TRG_OINV_Reserve_ToOdooQueue was not found on the expected table.', 1;

DECLARE @StlReserveDefinitionWasDisabled BIT =
(
    SELECT is_disabled FROM [SBO_STL_GOLIVE].sys.triggers
    WHERE object_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
);

DECLARE @StlReserveDefinition NVARCHAR(MAX)=N'ALTER TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
ON dbo.OINV
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO TEMP_API.dbo.INT_OdooQueue
    (
        ObjectType,
        ObjectKey,
        ActionType,
        SiteId,
        CompanyName,
        Status,
        RetryCount,
        CreateDate
    )
    SELECT
        ''ARReserveInvoice'',
        CAST(I.DocEntry AS NVARCHAR(50)),
        A.ActionType,
        ''PRD'',
        ''STL'',
        ''N'',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    CROSS APPLY
    (
        SELECT CASE
            WHEN D.DocEntry IS NULL AND ISNULL(I.CANCELED, ''N'') <> ''C'' THEN ''ADD''
            WHEN I.CANCELED = ''Y'' AND ISNULL(D.CANCELED, ''N'') <> ''Y'' THEN ''CANCEL''
        END AS ActionType
    ) A
    WHERE I.isIns = ''Y''
      AND I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)
      AND A.ActionType IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM TEMP_API.dbo.INT_OdooQueue Q
          WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = ''ARReserveInvoice''
            AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
            AND Q.ActionType COLLATE DATABASE_DEFAULT = A.ActionType COLLATE DATABASE_DEFAULT
            AND Q.SiteId COLLATE DATABASE_DEFAULT = ''PRD''
            AND Q.CompanyName COLLATE DATABASE_DEFAULT = ''STL''
            AND Q.Status COLLATE DATABASE_DEFAULT IN (''N'', ''P'')
      );
END;
';

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256',definition)=0x8086D7288C351699CF759B4449F6910D87B3DB6D99A4387A8C7020E112746A20
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(@ProductionDefinition,NCHAR(13),N''))
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(STUFF(@ProductionDefinition,1,5,N'CREATE'),NCHAR(13),N''))
      )
)
    THROW 51210, 'TIF_GOLIVE.TRG_OWOR_ToOdooQueue changed. Review the deployed definition first.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256',definition)=0x4060CA9D9DE49E2D197972CA1F33FA8AD54D81EC5E2F66B5C95D1B8982611423
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(@TifReserveDefinition,NCHAR(13),N''))
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(STUFF(@TifReserveDefinition,1,5,N'CREATE'),NCHAR(13),N''))
      )
)
    THROW 51211, 'TIF_GOLIVE.TRG_OINV_Reserve_ToOdooQueue changed. Review the deployed definition first.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [SBO_STL_GOLIVE].sys.sql_modules
    WHERE object_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256',definition)=0x5DD9DB2AC75407B7E6A54A37558B69641E363A26F0D2AA76232DE62D458C6BE5
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(@StlReserveDefinition,NCHAR(13),N''))
          OR HASHBYTES('SHA2_256',REPLACE(definition,NCHAR(13),N''))
             = HASHBYTES('SHA2_256',REPLACE(STUFF(@StlReserveDefinition,1,5,N'CREATE'),NCHAR(13),N''))
      )
)
    THROW 51212, 'SBO_STL_GOLIVE.TRG_OINV_Reserve_ToOdooQueue changed. Review the deployed definition first.', 1;

DECLARE @Candidates TABLE
(
    SapDatabaseName NVARCHAR(50) NOT NULL,
    CanonicalSiteId NVARCHAR(10) NOT NULL,
    CompanyName NVARCHAR(10) NOT NULL,
    ObjectType NVARCHAR(30) NOT NULL,
    DocEntry INT NOT NULL,
    DocNum INT NOT NULL,
    PostingDate DATETIME NULL,
    DueDate DATETIME NOT NULL,
    DocumentStatus CHAR(1) NOT NULL,
    ActionType NVARCHAR(20) NOT NULL,
    HistoryRows INT NOT NULL,
    PendingRows INT NOT NULL,
    ConfirmedRows INT NOT NULL,
    Decision NVARCHAR(40) NOT NULL,
    PRIMARY KEY (SapDatabaseName,ObjectType,DocEntry,ActionType)
);

DECLARE @Inserted TABLE
(
    QueueId INT PRIMARY KEY,
    ObjectType NVARCHAR(50),
    ObjectKey NVARCHAR(50),
    ActionType NVARCHAR(20),
    SapDatabaseName NVARCHAR(50)
);

BEGIN TRY
    IF @Apply=1
    BEGIN
        BEGIN TRANSACTION;

        EXEC @AppLockResult=sys.sp_getapplock
            @Resource=N'TIF_PRD_DueDateBackfill_20261109',
            @LockMode=N'Exclusive',
            @LockOwner=N'Transaction',
            @LockTimeout=0;

        IF @AppLockResult<0
            THROW 51220, 'Another DueDate cutover/backfill is running.', 1;

        EXEC [TIF_GOLIVE].sys.sp_executesql @ProductionDefinition;
        IF @ProductionDefinitionWasDisabled=1
            EXEC [TIF_GOLIVE].sys.sp_executesql
                N'DISABLE TRIGGER [dbo].[TRG_OWOR_ToOdooQueue] ON [dbo].[OWOR];';

        EXEC [TIF_GOLIVE].sys.sp_executesql @TifReserveDefinition;
        IF @TifReserveDefinitionWasDisabled=1
            EXEC [TIF_GOLIVE].sys.sp_executesql
                N'DISABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];';

        EXEC [SBO_STL_GOLIVE].sys.sp_executesql @StlReserveDefinition;
        IF @StlReserveDefinitionWasDisabled=1
            EXEC [SBO_STL_GOLIVE].sys.sp_executesql
                N'DISABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];';

        -- Lock the queue only after ALTER has acquired the PRD schema locks.
        -- This avoids a queue-first / SAP-table-first lock-order inversion.
        SELECT @QueueRowsLocked=COUNT_BIG(*)
        FROM dbo.INT_OdooQueue WITH (TABLOCKX,HOLDLOCK);

        IF EXISTS
        (
            SELECT 1 FROM dbo.INT_OdooQueue
            WHERE SiteId IN ('PRD','PRD-TIF','PRD-STL') AND Status IN ('N','P')
        )
            THROW 51221, 'PRD work is pending/in-flight. Let the existing Worker finish and re-preview.', 1;
    END;

    ;
WITH Candidates AS
(
 SELECT 'TIF_GOLIVE' AS SapDatabaseName,'PRD-TIF' AS CanonicalSiteId,'TIF' AS CompanyName,'ProductionOrder' AS ObjectType,
 H.DocEntry,H.DocNum,H.PostDate AS PostingDate,H.DueDate AS DueDate,H.Status AS DocumentStatus,
 CASE WHEN H.Status='C' THEN 'CANCEL' ELSE 'RELEASED' END AS ActionType
 FROM [TIF_GOLIVE].dbo.OWOR H WHERE H.DueDate>=CONVERT(DATE,'20261109',112) AND H.Status='R'
 UNION ALL
 SELECT 'TIF_GOLIVE','PRD-TIF','TIF','ARReserveInvoice',H.DocEntry,H.DocNum,H.DocDate,H.DocDueDate,H.CANCELED,
 CASE WHEN H.CANCELED='Y' THEN 'CANCEL' ELSE 'ADD' END
 FROM [TIF_GOLIVE].dbo.OINV H WHERE H.isIns='Y' AND H.DocDueDate>=CONVERT(DATE,'20261109',112) AND H.CANCELED='N' AND H.DocStatus='O'
 UNION ALL
 SELECT 'SBO_STL_GOLIVE','PRD-STL','STL','ARReserveInvoice',H.DocEntry,H.DocNum,H.DocDate,H.DocDueDate,H.CANCELED,
 CASE WHEN H.CANCELED='Y' THEN 'CANCEL' ELSE 'ADD' END
 FROM [SBO_STL_GOLIVE].dbo.OINV H WHERE H.isIns='Y' AND H.DocDueDate>=CONVERT(DATE,'20261109',112) AND H.CANCELED='N' AND H.DocStatus='O'
), Classified AS
(
 SELECT C.*,H.HistoryRows,H.PendingRows,H.ConfirmedRows,
 CASE WHEN H.PendingRows>0 THEN 'SKIP_PENDING' WHEN H.ConfirmedRows>0 THEN 'SKIP_SUCCESS' WHEN H.HistoryRows=0 THEN 'QUEUE_NEW' ELSE 'REVIEW_PREVIOUS_ATTEMPT' END AS Decision
 FROM Candidates C
 OUTER APPLY
 (
  SELECT COUNT(*) AS HistoryRows,
  ISNULL(SUM(CASE WHEN Q.Status IN('N','P') THEN 1 ELSE 0 END),0) AS PendingRows,
  ISNULL(SUM(CASE WHEN Q.Status='S'
    AND TRY_CONVERT(INT,JSON_VALUE(CASE WHEN ISJSON(Q.ResponseJson)=1 THEN Q.ResponseJson ELSE N'{}' END,'$.result.status')) BETWEEN 200 AND 299
    AND JSON_VALUE(CASE WHEN ISJSON(Q.ResponseJson)=1 THEN Q.ResponseJson ELSE N'{}' END,'$.result.results')='true' THEN 1 ELSE 0 END),0) AS ConfirmedRows
  FROM dbo.INT_OdooQueue Q
  WHERE Q.ObjectType COLLATE DATABASE_DEFAULT=C.ObjectType COLLATE DATABASE_DEFAULT
  AND Q.ObjectKey COLLATE DATABASE_DEFAULT=CONVERT(NVARCHAR(50),C.DocEntry) COLLATE DATABASE_DEFAULT
  AND Q.CompanyName COLLATE DATABASE_DEFAULT=C.CompanyName COLLATE DATABASE_DEFAULT
  AND Q.SiteId COLLATE DATABASE_DEFAULT IN('PRD',C.CanonicalSiteId COLLATE DATABASE_DEFAULT)
  AND ((C.ActionType='CANCEL' AND Q.ActionType='CANCEL')
   OR(C.ActionType='RELEASED' AND Q.ActionType IN('RELEASED','RELEASE'))
   OR(C.ActionType='ADD' AND Q.ActionType IN('ADD','RELEASED','RELEASE')))
 ) H
)
    INSERT INTO @Candidates
    SELECT SapDatabaseName,CanonicalSiteId,CompanyName,ObjectType,DocEntry,DocNum,
           PostingDate,DueDate,DocumentStatus,ActionType,HistoryRows,PendingRows,
           ConfirmedRows,Decision
    FROM Classified;

    IF @Apply=1
    BEGIN
        IF EXISTS (SELECT 1 FROM @Candidates WHERE Decision='REVIEW_PREVIOUS_ATTEMPT')
            THROW 51222, 'Some eligible documents have ambiguous previous attempts. Review them before backfill.', 1;

        IF (SELECT COUNT(*) FROM @Candidates WHERE Decision='QUEUE_NEW')>@MaximumBackfillRows
            THROW 51223, 'Backfill exceeds the reviewed maximum. Review the preview before increasing the bound.', 1;

        -- Prioritise Production Orders so the previously skipped releases go first.
        INSERT INTO dbo.INT_OdooQueue
        (
            ObjectType,ObjectKey,ActionType,Status,RetryCount,CreateDate,
            ProcessBy,SiteId,SapDatabaseName,CompanyName
        )
        OUTPUT INSERTED.QueueId,INSERTED.ObjectType,INSERTED.ObjectKey,
               INSERTED.ActionType,INSERTED.SapDatabaseName
        INTO @Inserted
        SELECT TOP (2147483647)
            ObjectType,CONVERT(NVARCHAR(50),DocEntry),ActionType,'N',0,GETDATE(),
            N'DueDateBackfill-20261109','PRD',SapDatabaseName,CompanyName
        FROM @Candidates
        WHERE Decision='QUEUE_NEW' AND ObjectType='ProductionOrder'
        ORDER BY DueDate,DocEntry;

        INSERT INTO dbo.INT_OdooQueue
        (
            ObjectType,ObjectKey,ActionType,Status,RetryCount,CreateDate,
            ProcessBy,SiteId,SapDatabaseName,CompanyName
        )
        OUTPUT INSERTED.QueueId,INSERTED.ObjectType,INSERTED.ObjectKey,
               INSERTED.ActionType,INSERTED.SapDatabaseName
        INTO @Inserted
        SELECT TOP (2147483647)
            ObjectType,CONVERT(NVARCHAR(50),DocEntry),ActionType,'N',0,GETDATE(),
            N'DueDateBackfill-20261109','PRD',SapDatabaseName,CompanyName
        FROM @Candidates
        WHERE Decision='QUEUE_NEW' AND ObjectType='ARReserveInvoice'
        ORDER BY DueDate,SapDatabaseName,DocEntry;

        COMMIT TRANSACTION;
    END;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT CASE WHEN @Apply=1 THEN 'APPLIED' ELSE 'PREVIEW_ONLY' END AS Mode,
       N'2026-11-09' AS DueDateCutoff,
       @BatchStarted AS BatchStarted,
       GETDATE() AS CheckedAt,
       (SELECT COUNT(*) FROM @Inserted) AS InsertedRows;

SELECT SapDatabaseName,ObjectType,ActionType,Decision,COUNT(*) AS Documents,
       MIN(DueDate) AS FirstDueDate,MAX(DueDate) AS LastDueDate
FROM @Candidates
GROUP BY SapDatabaseName,ObjectType,ActionType,Decision
ORDER BY SapDatabaseName,ObjectType,ActionType,Decision;

SELECT TOP(10) SapDatabaseName,ObjectType,DocEntry,DocNum,PostingDate,DueDate,
       DocumentStatus,ActionType,Decision
FROM @Candidates
ORDER BY CASE WHEN ObjectType='ProductionOrder' THEN 0 ELSE 1 END,DueDate,DocEntry;

SELECT QueueId,ObjectType,ObjectKey,ActionType,SapDatabaseName
FROM @Inserted ORDER BY QueueId;

SELECT N'TIF_GOLIVE' AS DatabaseName,tr.name AS TriggerName,
       CAST(tr.is_disabled AS INT) AS IsDisabled,
       N'OWOR.DueDate' AS DueDateColumn,
       CASE WHEN CHARINDEX(N'I.DueDate >= CONVERT(DATE, ''20261109'', 112)',m.definition)>0 THEN 1 ELSE 0 END AS DueDateFilterApplied
FROM [TIF_GOLIVE].sys.triggers tr
JOIN [TIF_GOLIVE].sys.sql_modules m ON m.object_id=tr.object_id
WHERE tr.object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue',N'TR')
UNION ALL
SELECT N'TIF_GOLIVE' AS DatabaseName,tr.name AS TriggerName,
       CAST(tr.is_disabled AS INT) AS IsDisabled,
       N'OINV.DocDueDate' AS DueDateColumn,
       CASE WHEN CHARINDEX(N'I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)',m.definition)>0 THEN 1 ELSE 0 END AS DueDateFilterApplied
FROM [TIF_GOLIVE].sys.triggers tr
JOIN [TIF_GOLIVE].sys.sql_modules m ON m.object_id=tr.object_id
WHERE tr.object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue',N'TR')
UNION ALL
SELECT N'SBO_STL_GOLIVE' AS DatabaseName,tr.name AS TriggerName,
       CAST(tr.is_disabled AS INT) AS IsDisabled,
       N'OINV.DocDueDate' AS DueDateColumn,
       CASE WHEN CHARINDEX(N'I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)',m.definition)>0 THEN 1 ELSE 0 END AS DueDateFilterApplied
FROM [SBO_STL_GOLIVE].sys.triggers tr
JOIN [SBO_STL_GOLIVE].sys.sql_modules m ON m.object_id=tr.object_id
WHERE tr.object_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue',N'TR')
ORDER BY DatabaseName,TriggerName;
GO


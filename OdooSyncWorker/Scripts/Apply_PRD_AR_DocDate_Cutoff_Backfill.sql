-- Current PRD rule: A/R Reserve Invoice DocDate >= 2026-11-09 (inclusive).
-- Only TIF_GOLIVE and SBO_STL_GOLIVE A/R triggers are changed.
-- Production Order and all TEST triggers keep their definitions and states.
-- Future events retain the existing ADD/CANCEL and deduplication behavior.
-- Backfill: only open, non-cancelled reserves with NO previous queue history.
-- Confirmed successes and N/P work are skipped. Other attempts require review.
-- Existing queue rows and SAP document data are never updated/reset/deleted.
-- Preview is the default. @Apply=1 updates triggers and queues new rows atomically.
-- The existing Worker sends committed rows; no deployment/restart is needed.

USE [TEMP_API];
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 10000;

DECLARE @Apply BIT = 0;
DECLARE @MaximumBackfillRows INT = 2000;
DECLARE @StartedAt DATETIME = GETDATE();
DECLARE @AppLockResult INT;
DECLARE @QueueRowsLocked BIGINT;
DECLARE @SapRowsLocked BIGINT;
DECLARE @OldPredicate NVARCHAR(100) = N'I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)';
DECLARE @NewPredicate NVARCHAR(100) = N'I.DocDate >= CONVERT(DATE, ''20261109'', 112)';

IF CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ServerName')) <> N'TIFSVR01-SAP'
    THROW 51300, 'Wrong SQL Server. Expected TIFSVR01-SAP.', 1;
IF OBJECT_ID(N'TEMP_API.dbo.INT_OdooQueue',N'U') IS NULL
    THROW 51301, 'TEMP_API.dbo.INT_OdooQueue was not found.', 1;
IF COL_LENGTH(N'TIF_GOLIVE.dbo.OINV',N'DocDate') IS NULL
   OR COL_LENGTH(N'SBO_STL_GOLIVE.dbo.OINV',N'DocDate') IS NULL
    THROW 51302, 'Required PRD A/R posting-date columns were not found.', 1;

DECLARE @Triggers TABLE
(
    DatabaseName SYSNAME PRIMARY KEY,
    WasDisabled BIT NOT NULL,
    OriginalDefinition NVARCHAR(MAX) NOT NULL,
    TargetDefinition NVARCHAR(MAX) NULL,
    NeedsChange BIT NULL
);
INSERT INTO @Triggers(DatabaseName,WasDisabled,OriginalDefinition)
SELECT N'TIF_GOLIVE',T.is_disabled,M.definition
FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.object_id=OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue',N'TR')
  AND T.parent_id=OBJECT_ID(N'TIF_GOLIVE.dbo.OINV',N'U')
UNION ALL
SELECT N'SBO_STL_GOLIVE',T.is_disabled,M.definition
FROM SBO_STL_GOLIVE.sys.triggers T JOIN SBO_STL_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.object_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue',N'TR')
  AND T.parent_id=OBJECT_ID(N'SBO_STL_GOLIVE.dbo.OINV',N'U');
IF (SELECT COUNT(*) FROM @Triggers)<>2
    THROW 51303, 'Both PRD A/R triggers must exist on dbo.OINV.', 1;

-- Patch only the reviewed date predicate. Refuse unknown/multiple predicates.
IF EXISTS
(
    SELECT 1 FROM @Triggers
    WHERE NOT
    (
      ((LEN(OriginalDefinition)-LEN(REPLACE(OriginalDefinition,@OldPredicate,N'')))/LEN(@OldPredicate)=1
        AND CHARINDEX(@NewPredicate,OriginalDefinition)=0)
      OR
      ((LEN(OriginalDefinition)-LEN(REPLACE(OriginalDefinition,@NewPredicate,N'')))/LEN(@NewPredicate)=1
        AND CHARINDEX(@OldPredicate,OriginalDefinition)=0)
    )
    OR LEFT(OriginalDefinition,14)<>N'CREATE TRIGGER'
)
    THROW 51304, 'Unexpected A/R trigger definition. Review before changing it.', 1;
UPDATE @Triggers
SET NeedsChange=CASE WHEN CHARINDEX(@OldPredicate,OriginalDefinition)>0 THEN 1 ELSE 0 END,
    TargetDefinition=STUFF(REPLACE(OriginalDefinition,@OldPredicate,@NewPredicate),1,6,N'ALTER');

DECLARE @Protected TABLE(DatabaseName SYSNAME,TriggerName SYSNAME,WasDisabled BIT,DefinitionHash VARBINARY(32),PRIMARY KEY(DatabaseName,TriggerName));
INSERT INTO @Protected
SELECT N'TIF_GOLIVE',T.name COLLATE DATABASE_DEFAULT,T.is_disabled,HASHBYTES('SHA2_256',M.definition)
FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.name=N'TRG_OWOR_ToOdooQueue'
UNION ALL
SELECT N'TEST_INTERFACE',T.name COLLATE DATABASE_DEFAULT,T.is_disabled,HASHBYTES('SHA2_256',M.definition)
FROM TEST_INTERFACE.sys.triggers T JOIN TEST_INTERFACE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.name IN(N'TRG_OWOR_ToOdooQueue',N'TRG_OINV_Reserve_ToOdooQueue')
UNION ALL
SELECT N'TEST_STL_ODOO',T.name COLLATE DATABASE_DEFAULT,T.is_disabled,HASHBYTES('SHA2_256',M.definition)
FROM TEST_STL_ODOO.sys.triggers T JOIN TEST_STL_ODOO.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.name=N'TRG_OINV_Reserve_ToOdooQueue';
IF (SELECT COUNT(*) FROM @Protected)<>4
    THROW 51311, 'Expected Production Order/TEST triggers were not found.', 1;

DECLARE @Candidates TABLE
(
    SapDatabaseName SYSNAME NOT NULL,CanonicalSiteId NVARCHAR(10) NOT NULL,CompanyName NVARCHAR(10) NOT NULL,
    DocEntry INT NOT NULL,DocNum INT NOT NULL,DocDate DATETIME NOT NULL,DocDueDate DATETIME NULL,
    HistoryRows INT NOT NULL,PendingRows INT NOT NULL,ConfirmedRows INT NOT NULL,Decision NVARCHAR(40) NOT NULL,
    PRIMARY KEY(SapDatabaseName,DocEntry)
);
DECLARE @Inserted TABLE(QueueId INT PRIMARY KEY,SapDatabaseName NVARCHAR(50),ObjectKey NVARCHAR(50));
DECLARE @Sql NVARCHAR(MAX);

BEGIN TRY
    IF @Apply=1
    BEGIN
        BEGIN TRANSACTION;
        EXEC @AppLockResult=sys.sp_getapplock
            @Resource=N'TIF_PRD_DueDateBackfill_20261109',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=0;
        IF @AppLockResult<0
            THROW 51305, 'Another PRD date cutover/backfill is running.', 1;

        IF EXISTS
        (
            SELECT DatabaseName,WasDisabled,HASHBYTES('SHA2_256',OriginalDefinition) FROM @Triggers
            EXCEPT
            SELECT A.DatabaseName,A.WasDisabled,A.DefinitionHash FROM
            (
                SELECT N'TIF_GOLIVE' AS DatabaseName,T.is_disabled AS WasDisabled,HASHBYTES('SHA2_256',M.definition) AS DefinitionHash
                FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
                WHERE T.name=N'TRG_OINV_Reserve_ToOdooQueue'
                UNION ALL
                SELECT N'SBO_STL_GOLIVE',T.is_disabled,HASHBYTES('SHA2_256',M.definition)
                FROM SBO_STL_GOLIVE.sys.triggers T JOIN SBO_STL_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
                WHERE T.name=N'TRG_OINV_Reserve_ToOdooQueue'
            ) A
        )
            THROW 51312, 'A/R trigger changed concurrently. Re-preview before applying.', 1;

        -- Acquire SAP schema locks before the queue lock, matching trigger lock order.
        IF EXISTS(SELECT 1 FROM @Triggers WHERE DatabaseName=N'TIF_GOLIVE' AND NeedsChange=1)
        BEGIN
            SELECT @Sql=TargetDefinition FROM @Triggers WHERE DatabaseName=N'TIF_GOLIVE';
            EXEC TIF_GOLIVE.sys.sp_executesql @Sql;
            IF EXISTS(SELECT 1 FROM @Triggers WHERE DatabaseName=N'TIF_GOLIVE' AND WasDisabled=1)
                EXEC TIF_GOLIVE.sys.sp_executesql N'DISABLE TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue ON dbo.OINV;';
        END;
        ELSE
            SELECT @SapRowsLocked=COUNT_BIG(*) FROM TIF_GOLIVE.dbo.OINV WITH(TABLOCK,HOLDLOCK);
        IF EXISTS(SELECT 1 FROM @Triggers WHERE DatabaseName=N'SBO_STL_GOLIVE' AND NeedsChange=1)
        BEGIN
            SELECT @Sql=TargetDefinition FROM @Triggers WHERE DatabaseName=N'SBO_STL_GOLIVE';
            EXEC SBO_STL_GOLIVE.sys.sp_executesql @Sql;
            IF EXISTS(SELECT 1 FROM @Triggers WHERE DatabaseName=N'SBO_STL_GOLIVE' AND WasDisabled=1)
                EXEC SBO_STL_GOLIVE.sys.sp_executesql N'DISABLE TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue ON dbo.OINV;';
        END;
        ELSE
            SELECT @SapRowsLocked=COUNT_BIG(*) FROM SBO_STL_GOLIVE.dbo.OINV WITH(TABLOCK,HOLDLOCK);
        SELECT @QueueRowsLocked=COUNT_BIG(*) FROM dbo.INT_OdooQueue WITH(TABLOCKX,HOLDLOCK);
        IF EXISTS(SELECT 1 FROM dbo.INT_OdooQueue WHERE SiteId IN('PRD','PRD-TIF','PRD-STL') AND Status IN('N','P'))
            THROW 51306, 'PRD work is pending/in-flight. Let the Worker finish and re-preview.', 1;
    END;

-- BEGIN BACKFILL SELECTION (shared with the fixture test)
    ;WITH C AS
    (
        SELECT N'TIF_GOLIVE' AS SapDatabaseName,N'PRD-TIF' AS CanonicalSiteId,N'TIF' AS CompanyName,
               H.DocEntry,H.DocNum,H.DocDate,H.DocDueDate
        FROM TIF_GOLIVE.dbo.OINV H
        WHERE H.isIns='Y' AND H.DocDate>=CONVERT(DATE,'20261109',112) AND H.CANCELED='N' AND H.DocStatus='O'
        UNION ALL
        SELECT N'SBO_STL_GOLIVE',N'PRD-STL',N'STL',H.DocEntry,H.DocNum,H.DocDate,H.DocDueDate
        FROM SBO_STL_GOLIVE.dbo.OINV H
        WHERE H.isIns='Y' AND H.DocDate>=CONVERT(DATE,'20261109',112) AND H.CANCELED='N' AND H.DocStatus='O'
    ), Q AS
    (
        SELECT Q.ObjectKey,Q.Status,
          CASE WHEN NULLIF(LTRIM(RTRIM(Q.SapDatabaseName)),N'') IS NOT NULL THEN Q.SapDatabaseName
               WHEN Q.SiteId='PRD-TIF' OR(Q.SiteId='PRD' AND Q.CompanyName='TIF') THEN N'TIF_GOLIVE'
               WHEN Q.SiteId='PRD-STL' OR(Q.SiteId='PRD' AND Q.CompanyName='STL') THEN N'SBO_STL_GOLIVE' END AS ResolvedDatabase,
          CASE WHEN ISJSON(Q.ResponseJson)=1 THEN Q.ResponseJson ELSE N'{}' END AS SafeResponse
        FROM dbo.INT_OdooQueue Q WHERE Q.ObjectType='ARReserveInvoice'
    )
    INSERT INTO @Candidates
    SELECT C.*,H.HistoryRows,H.PendingRows,H.ConfirmedRows,
           CASE WHEN H.PendingRows>0 THEN N'SKIP_PENDING' WHEN H.ConfirmedRows>0 THEN N'SKIP_SUCCESS'
                WHEN H.HistoryRows=0 THEN N'QUEUE_NEW' ELSE N'REVIEW_PREVIOUS_ATTEMPT' END
    FROM C OUTER APPLY
    (
        SELECT COUNT(*) AS HistoryRows,
          ISNULL(SUM(CASE WHEN Q.Status IN('N','P') THEN 1 ELSE 0 END),0) AS PendingRows,
          ISNULL(SUM(CASE WHEN Q.Status='S'
              AND TRY_CONVERT(INT,JSON_VALUE(Q.SafeResponse,'$.result.status')) BETWEEN 200 AND 299
              AND JSON_VALUE(Q.SafeResponse,'$.result.results')='true' THEN 1 ELSE 0 END),0) AS ConfirmedRows
        FROM Q WHERE Q.ResolvedDatabase COLLATE DATABASE_DEFAULT=C.SapDatabaseName COLLATE DATABASE_DEFAULT
          AND TRY_CONVERT(INT,Q.ObjectKey)=C.DocEntry
    ) H;
-- END BACKFILL SELECTION

    IF @Apply=1
    BEGIN
        IF EXISTS(SELECT 1 FROM @Candidates WHERE Decision=N'REVIEW_PREVIOUS_ATTEMPT')
            THROW 51307, 'Eligible documents have unconfirmed previous attempts. Review instead of replaying.', 1;
        IF (SELECT COUNT(*) FROM @Candidates WHERE Decision=N'QUEUE_NEW')>@MaximumBackfillRows
            THROW 51308, 'Backfill exceeds the reviewed maximum. Review before increasing the bound.', 1;

        INSERT INTO dbo.INT_OdooQueue
            (ObjectType,ObjectKey,ActionType,SiteId,CompanyName,SapDatabaseName,Status,RetryCount,CreateDate,ProcessBy)
        OUTPUT inserted.QueueId,inserted.SapDatabaseName,inserted.ObjectKey INTO @Inserted
        SELECT TOP(2147483647) N'ARReserveInvoice',CONVERT(NVARCHAR(50),DocEntry),N'ADD',N'PRD',CompanyName,
               SapDatabaseName,N'N',0,GETDATE(),N'ARDocDateBackfill-20261109'
        FROM @Candidates WHERE Decision=N'QUEUE_NEW' ORDER BY DocDate,SapDatabaseName,DocEntry;

        -- Verify the two AR bodies/states, and assert Production/TEST were preserved.
        IF EXISTS
        (
            SELECT 1 FROM @Triggers B
            CROSS APPLY(SELECT M.definition,T.is_disabled FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
                        WHERE B.DatabaseName=N'TIF_GOLIVE' AND T.name=N'TRG_OINV_Reserve_ToOdooQueue'
                        UNION ALL SELECT M.definition,T.is_disabled FROM SBO_STL_GOLIVE.sys.triggers T JOIN SBO_STL_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
                        WHERE B.DatabaseName=N'SBO_STL_GOLIVE' AND T.name=N'TRG_OINV_Reserve_ToOdooQueue') A
            WHERE HASHBYTES('SHA2_256',A.definition)<>HASHBYTES('SHA2_256',REPLACE(B.OriginalDefinition,@OldPredicate,@NewPredicate))
               OR A.is_disabled<>B.WasDisabled
        )
            THROW 51309, 'A/R trigger body/state verification failed.', 1;
        IF EXISTS
        (
            SELECT DatabaseName,TriggerName,WasDisabled,DefinitionHash FROM @Protected
            EXCEPT
            SELECT P.DatabaseName,P.TriggerName,P.WasDisabled,P.DefinitionHash
            FROM
            (
                SELECT N'TIF_GOLIVE' AS DatabaseName,T.name COLLATE DATABASE_DEFAULT AS TriggerName,T.is_disabled AS WasDisabled,HASHBYTES('SHA2_256',M.definition) AS DefinitionHash
                FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id WHERE T.name=N'TRG_OWOR_ToOdooQueue'
                UNION ALL
                SELECT N'TEST_INTERFACE',T.name COLLATE DATABASE_DEFAULT,T.is_disabled,HASHBYTES('SHA2_256',M.definition)
                FROM TEST_INTERFACE.sys.triggers T JOIN TEST_INTERFACE.sys.sql_modules M ON M.object_id=T.object_id WHERE T.name IN(N'TRG_OWOR_ToOdooQueue',N'TRG_OINV_Reserve_ToOdooQueue')
                UNION ALL
                SELECT N'TEST_STL_ODOO',T.name COLLATE DATABASE_DEFAULT,T.is_disabled,HASHBYTES('SHA2_256',M.definition)
                FROM TEST_STL_ODOO.sys.triggers T JOIN TEST_STL_ODOO.sys.sql_modules M ON M.object_id=T.object_id WHERE T.name=N'TRG_OINV_Reserve_ToOdooQueue'
            ) P
        )
            THROW 51310, 'Production Order or TEST trigger changed during this operation.', 1;
        COMMIT TRANSACTION;
    END;
END TRY
BEGIN CATCH
    IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT CASE WHEN @Apply=1 THEN 'APPLIED' ELSE 'PREVIEW_ONLY' END AS Mode,N'2026-11-09' AS ArDocDateCutoff,
       @StartedAt AS StartedAt,GETDATE() AS CheckedAt,(SELECT COUNT(*) FROM @Inserted) AS InsertedRows;
SELECT SapDatabaseName,Decision,COUNT(*) AS Documents,MIN(DocDate) AS FirstDocDate,MAX(DocDate) AS LastDocDate
FROM @Candidates GROUP BY SapDatabaseName,Decision ORDER BY SapDatabaseName,Decision;
SELECT QueueId,SapDatabaseName,ObjectKey FROM @Inserted ORDER BY QueueId;
SELECT SapDatabaseName,DocEntry,DocNum,DocDate,DocDueDate,HistoryRows,PendingRows,ConfirmedRows,Decision
FROM @Candidates WHERE Decision IN(N'QUEUE_NEW',N'REVIEW_PREVIOUS_ATTEMPT') ORDER BY SapDatabaseName,DocEntry;
SELECT DatabaseName,WasDisabled AS IsDisabled,NeedsChange AS TriggerChangedWhenApplying FROM @Triggers ORDER BY DatabaseName;
GO

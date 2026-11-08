-- LEGACY: current A/R changes use Apply_PRD_AR_DocDate_Cutoff_Backfill.sql.
-- Retained for deployment history. Do not use this file for the current rule.
-- PRD SAP -> Odoo document posting-date cutoff: 2026-11-07 (inclusive).
-- Run the entire file in SSMS on TIFSVR01-SAP before first PRD activation.
-- Production Order uses OWOR.PostDate; Reserve Invoice uses OINV.DocDate.
-- The date filter applies to Release/Add and Cancel, regardless of event time.
-- All three triggers MUST already be disabled; they remain disabled on success.
-- Definitions are based on the deployed PRD triggers reviewed on 2026-11-06.
-- Stop if their bodies have changed instead of overwriting an unknown version.
-- Preserve deployed SiteId='PRD' and CompanyName='TIF'/'STL'.
-- TEST, existing queue rows and SAP document data are not changed or backfilled.
-- After verification, use Enable_PRD_Queue_Triggers.sql to start queueing.

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ServerName')) <> N'TIFSVR01-SAP'
    THROW 51100, 'Wrong SQL Server. Expected TIFSVR01-SAP.', 1;

IF DB_ID(N'TIF_GOLIVE') IS NULL OR DB_ID(N'SBO_STL_GOLIVE') IS NULL
    THROW 51101, 'Required PRD SAP databases were not found.', 1;

IF OBJECT_ID(N'TEMP_API.dbo.INT_OdooQueue', N'U') IS NULL
    THROW 51102, 'TEMP_API.dbo.INT_OdooQueue was not found.', 1;

IF COL_LENGTH(N'TIF_GOLIVE.dbo.OWOR', N'PostDate') IS NULL
   OR COL_LENGTH(N'TIF_GOLIVE.dbo.OINV', N'DocDate') IS NULL
   OR COL_LENGTH(N'SBO_STL_GOLIVE.dbo.OINV', N'DocDate') IS NULL
    THROW 51103, 'Required SAP document posting-date columns were not found.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
      AND parent_id = OBJECT_ID(N'TIF_GOLIVE.dbo.OWOR', N'U')
      AND is_disabled = 1
)
    THROW 51104, 'TIF_GOLIVE.TRG_OWOR_ToOdooQueue must exist on OWOR and be disabled first.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.triggers
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND parent_id = OBJECT_ID(N'TIF_GOLIVE.dbo.OINV', N'U')
      AND is_disabled = 1
)
    THROW 51105, 'TIF_GOLIVE.TRG_OINV_Reserve_ToOdooQueue must exist on OINV and be disabled first.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [SBO_STL_GOLIVE].sys.triggers
    WHERE object_id = OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND parent_id = OBJECT_ID(N'SBO_STL_GOLIVE.dbo.OINV', N'U')
      AND is_disabled = 1
)
    THROW 51106, 'SBO_STL_GOLIVE.TRG_OINV_Reserve_ToOdooQueue must exist on OINV and be disabled first.', 1;

DECLARE @ProductionDefinition NVARCHAR(MAX) = N'ALTER TRIGGER dbo.TRG_OWOR_ToOdooQueue
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
    WHERE I.PostDate >= CONVERT(DATE, ''20261107'', 112)
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

DECLARE @TifReserveDefinition NVARCHAR(MAX) = N'ALTER TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
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
      AND I.DocDate >= CONVERT(DATE, ''20261107'', 112)
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

DECLARE @StlReserveDefinition NVARCHAR(MAX) = N'ALTER TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
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
      AND I.DocDate >= CONVERT(DATE, ''20261107'', 112)
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

-- Accept only the exact reviewed baseline or this file's already-applied body.
-- Ignore CR characters only when comparing an already-applied body.
IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256', definition) = 0xAAF2AFF3B703C1B19F901463D7832AC452E60BEE4CFD3957651EE7FE928593CF
          OR HASHBYTES('SHA2_256', REPLACE(definition, NCHAR(13), N''))
             = HASHBYTES('SHA2_256', REPLACE(@ProductionDefinition, NCHAR(13), N''))
      )
)
    THROW 51110, 'TIF_GOLIVE.TRG_OWOR_ToOdooQueue definition changed. Review it before applying this file.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256', definition) = 0x18728B2C3241161F876A6CA9DE30C1625F8D924004F22BB978A4DD663799C385
          OR HASHBYTES('SHA2_256', REPLACE(definition, NCHAR(13), N''))
             = HASHBYTES('SHA2_256', REPLACE(@TifReserveDefinition, NCHAR(13), N''))
      )
)
    THROW 51111, 'TIF_GOLIVE.TRG_OINV_Reserve_ToOdooQueue definition changed. Review it before applying this file.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [SBO_STL_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND
      (
          HASHBYTES('SHA2_256', definition) = 0x34BBB48B34605341926207374E187F65538013E9D3091275EDAE948A4ABBF654
          OR HASHBYTES('SHA2_256', REPLACE(definition, NCHAR(13), N''))
             = HASHBYTES('SHA2_256', REPLACE(@StlReserveDefinition, NCHAR(13), N''))
      )
)
    THROW 51112, 'SBO_STL_GOLIVE.TRG_OINV_Reserve_ToOdooQueue definition changed. Review it before applying this file.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    -- ALTER TRIGGER enables a trigger, so disable it again before committing.
    EXEC [TIF_GOLIVE].sys.sp_executesql @ProductionDefinition;
    EXEC [TIF_GOLIVE].sys.sp_executesql
        N'DISABLE TRIGGER [dbo].[TRG_OWOR_ToOdooQueue] ON [dbo].[OWOR];';

    EXEC [TIF_GOLIVE].sys.sp_executesql @TifReserveDefinition;
    EXEC [TIF_GOLIVE].sys.sp_executesql
        N'DISABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];';

    EXEC [SBO_STL_GOLIVE].sys.sp_executesql @StlReserveDefinition;
    EXEC [SBO_STL_GOLIVE].sys.sp_executesql
        N'DISABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];';

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;

-- Expected: three rows, is_disabled=1, DateFilterApplied=1.
SELECT
    N'TIF_GOLIVE' AS DatabaseName,
    tr.name AS TriggerName,
    tr.is_disabled,
    N'OWOR.PostDate' AS DateColumn,
    N'2026-11-07' AS PostingDateCutoff,
    CASE WHEN CHARINDEX(N'I.PostDate >= CONVERT(DATE, ''20261107'', 112)', m.definition) > 0
         THEN 1 ELSE 0 END AS DateFilterApplied
FROM [TIF_GOLIVE].sys.triggers AS tr
JOIN [TIF_GOLIVE].sys.sql_modules AS m ON m.object_id = tr.object_id
WHERE tr.object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')

UNION ALL

SELECT
    N'TIF_GOLIVE' AS DatabaseName,
    tr.name AS TriggerName,
    tr.is_disabled,
    N'OINV.DocDate' AS DateColumn,
    N'2026-11-07' AS PostingDateCutoff,
    CASE WHEN CHARINDEX(N'I.DocDate >= CONVERT(DATE, ''20261107'', 112)', m.definition) > 0
         THEN 1 ELSE 0 END AS DateFilterApplied
FROM [TIF_GOLIVE].sys.triggers AS tr
JOIN [TIF_GOLIVE].sys.sql_modules AS m ON m.object_id = tr.object_id
WHERE tr.object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')

UNION ALL

SELECT
    N'SBO_STL_GOLIVE' AS DatabaseName,
    tr.name AS TriggerName,
    tr.is_disabled,
    N'OINV.DocDate' AS DateColumn,
    N'2026-11-07' AS PostingDateCutoff,
    CASE WHEN CHARINDEX(N'I.DocDate >= CONVERT(DATE, ''20261107'', 112)', m.definition) > 0
         THEN 1 ELSE 0 END AS DateFilterApplied
FROM [SBO_STL_GOLIVE].sys.triggers AS tr
JOIN [SBO_STL_GOLIVE].sys.sql_modules AS m ON m.object_id = tr.object_id
WHERE tr.object_id = OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
ORDER BY DatabaseName, TriggerName;
GO


-- OdooSyncWorker: enable PRD queue triggers for SAP -> Odoo.
-- Run this entire script in SSMS on TIFSVR01-SAP.
-- Apply_PRD_AR_DocDate_Cutoff_Backfill.sql updates the two existing A/R triggers.
-- Requires Production DueDate >= 2026-11-09 and A/R DocDate >= 2026-11-09.
-- The running Worker can send newly queued PRD events immediately.
-- Existing SAP documents are not backfilled by this script.
-- TEST triggers, SalesOrder and ItemMaster triggers are not changed.

USE [master];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF CONVERT(NVARCHAR(128), SERVERPROPERTY(N'ServerName')) <> N'TIFSVR01-SAP'
    THROW 51000, 'Wrong SQL Server. Expected TIFSVR01-SAP.', 1;

IF DB_ID(N'TIF_GOLIVE') IS NULL OR DB_ID(N'SBO_STL_GOLIVE') IS NULL
    THROW 51001, 'Required PRD SAP databases were not found.', 1;

IF OBJECT_ID(N'TEMP_API.dbo.INT_OdooQueue', N'U') IS NULL
    THROW 51002, 'TEMP_API.dbo.INT_OdooQueue was not found.', 1;

IF OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR') IS NULL
    THROW 51003, 'TIF_GOLIVE Production Order queue trigger was not found.', 1;

IF OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR') IS NULL
    THROW 51004, 'TIF_GOLIVE Reserve Invoice queue trigger was not found.', 1;

IF OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR') IS NULL
    THROW 51005, 'SBO_STL_GOLIVE Reserve Invoice queue trigger was not found.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OWOR_ToOdooQueue', N'TR')
      AND CHARINDEX(N'I.DueDate >= CONVERT(DATE, ''20261109'', 112)', definition) > 0
)
    THROW 51006, 'Production Order due-date rule is missing. Review its existing definition separately.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [TIF_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'TIF_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND CHARINDEX(N'I.DocDate >= CONVERT(DATE, ''20261109'', 112)', definition) > 0
)
    THROW 51007, 'TIF Reserve Invoice posting-date rule is missing. Run Apply_PRD_AR_DocDate_Cutoff_Backfill.sql first.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM [SBO_STL_GOLIVE].sys.sql_modules
    WHERE object_id = OBJECT_ID(N'SBO_STL_GOLIVE.dbo.TRG_OINV_Reserve_ToOdooQueue', N'TR')
      AND CHARINDEX(N'I.DocDate >= CONVERT(DATE, ''20261109'', 112)', definition) > 0
)
    THROW 51008, 'STL Reserve Invoice posting-date rule is missing. Run Apply_PRD_AR_DocDate_Cutoff_Backfill.sql first.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    USE [TIF_GOLIVE];
    ENABLE TRIGGER [dbo].[TRG_OWOR_ToOdooQueue] ON [dbo].[OWOR];
    ENABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];

    USE [SBO_STL_GOLIVE];
    ENABLE TRIGGER [dbo].[TRG_OINV_Reserve_ToOdooQueue] ON [dbo].[OINV];

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;

-- Expected result: exactly three rows, each with is_disabled = 0.
SELECT
    N'TIF_GOLIVE' AS DatabaseName,
    name AS TriggerName,
    is_disabled
FROM [TIF_GOLIVE].sys.triggers
WHERE name IN
(
    N'TRG_OWOR_ToOdooQueue',
    N'TRG_OINV_Reserve_ToOdooQueue'
)

UNION ALL

SELECT
    N'SBO_STL_GOLIVE',
    name,
    is_disabled
FROM [SBO_STL_GOLIVE].sys.triggers
WHERE name = N'TRG_OINV_Reserve_ToOdooQueue'
ORDER BY DatabaseName, TriggerName;
GO

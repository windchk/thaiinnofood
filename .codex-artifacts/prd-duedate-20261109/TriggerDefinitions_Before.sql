-- Trigger definition snapshot before the DueDate >= 2026-11-09 cutover.
-- Captured from TIFSVR01-SAP on 2026-11-07; this contains historical DDL.
-- Restoration requires review and does not undo any queued/sent Odoo work.

-- TIF_GOLIVE.TRG_OINV_Reserve_ToOdooQueue; is_disabled=0; SHA256=4060CA9D9DE49E2D197972CA1F33FA8AD54D81EC5E2F66B5C95D1B8982611423
USE [TIF_GOLIVE];
GO
CREATE TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
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
        'ARReserveInvoice',
        CAST(I.DocEntry AS NVARCHAR(50)),
        A.ActionType,
        'PRD',
        'TIF',
        'N',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    CROSS APPLY
    (
        SELECT CASE
            WHEN D.DocEntry IS NULL AND ISNULL(I.CANCELED, 'N') <> 'C' THEN 'ADD'
            WHEN I.CANCELED = 'Y' AND ISNULL(D.CANCELED, 'N') <> 'Y' THEN 'CANCEL'
        END AS ActionType
    ) A
    WHERE I.isIns = 'Y'
      AND I.DocDate >= CONVERT(DATE, '20261107', 112)
      AND A.ActionType IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM TEMP_API.dbo.INT_OdooQueue Q
          WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = 'ARReserveInvoice'
            AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
            AND Q.ActionType COLLATE DATABASE_DEFAULT = A.ActionType COLLATE DATABASE_DEFAULT
            AND Q.SiteId COLLATE DATABASE_DEFAULT = 'PRD'
            AND Q.CompanyName COLLATE DATABASE_DEFAULT = 'TIF'
            AND Q.Status COLLATE DATABASE_DEFAULT IN ('N', 'P')
      );
END;
GO

-- TIF_GOLIVE.TRG_OWOR_ToOdooQueue; is_disabled=0; SHA256=8086D7288C351699CF759B4449F6910D87B3DB6D99A4387A8C7020E112746A20
USE [TIF_GOLIVE];
GO
CREATE TRIGGER dbo.TRG_OWOR_ToOdooQueue
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
        'ProductionOrder',
        CAST(I.DocEntry AS NVARCHAR(50)),
        CASE I.Status WHEN 'R' THEN 'RELEASED' ELSE 'CANCEL' END,
        'PRD',
        'TIF',
        'N',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    WHERE I.PostDate >= CONVERT(DATE, '20261107', 112)
        AND
        (
            (
                I.Status = 'R'
                AND ISNULL(D.Status, '') <> 'R'
            )
            OR
            (
                I.Status = 'C'
                AND ISNULL(D.Status, '') <> 'C'
            )
        )
        AND NOT EXISTS
        (
            SELECT 1
            FROM TEMP_API.dbo.INT_OdooQueue Q
            WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = 'ProductionOrder'
              AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
              AND Q.ActionType COLLATE DATABASE_DEFAULT = CASE I.Status WHEN 'R' THEN 'RELEASED' ELSE 'CANCEL' END
              AND Q.SiteId COLLATE DATABASE_DEFAULT = 'PRD'
              AND Q.CompanyName COLLATE DATABASE_DEFAULT = 'TIF'
              AND Q.Status COLLATE DATABASE_DEFAULT IN ('N', 'P')
        );
END;
GO

-- SBO_STL_GOLIVE.TRG_OINV_Reserve_ToOdooQueue; is_disabled=0; SHA256=5DD9DB2AC75407B7E6A54A37558B69641E363A26F0D2AA76232DE62D458C6BE5
USE [SBO_STL_GOLIVE];
GO
CREATE TRIGGER dbo.TRG_OINV_Reserve_ToOdooQueue
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
        'ARReserveInvoice',
        CAST(I.DocEntry AS NVARCHAR(50)),
        A.ActionType,
        'PRD',
        'STL',
        'N',
        0,
        GETDATE()
    FROM inserted I
    LEFT JOIN deleted D
        ON I.DocEntry = D.DocEntry
    CROSS APPLY
    (
        SELECT CASE
            WHEN D.DocEntry IS NULL AND ISNULL(I.CANCELED, 'N') <> 'C' THEN 'ADD'
            WHEN I.CANCELED = 'Y' AND ISNULL(D.CANCELED, 'N') <> 'Y' THEN 'CANCEL'
        END AS ActionType
    ) A
    WHERE I.isIns = 'Y'
      AND I.DocDate >= CONVERT(DATE, '20261107', 112)
      AND A.ActionType IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM TEMP_API.dbo.INT_OdooQueue Q
          WHERE Q.ObjectType COLLATE DATABASE_DEFAULT = 'ARReserveInvoice'
            AND Q.ObjectKey COLLATE DATABASE_DEFAULT = CAST(I.DocEntry AS NVARCHAR(50))
            AND Q.ActionType COLLATE DATABASE_DEFAULT = A.ActionType COLLATE DATABASE_DEFAULT
            AND Q.SiteId COLLATE DATABASE_DEFAULT = 'PRD'
            AND Q.CompanyName COLLATE DATABASE_DEFAULT = 'STL'
            AND Q.Status COLLATE DATABASE_DEFAULT IN ('N', 'P')
      );
END;
GO


SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
DECLARE @Apply BIT = 0;

IF DB_NAME() <> N'TEMP_API' OR @@SERVERNAME <> N'TIFSVR01-SAP'
    THROW 51000, 'Unexpected log database/server; no change applied.', 1;

DECLARE @OldDefinition NVARCHAR(MAX) = N'([Status]=''COMPLETED'' OR [Status]=''GR_PENDING'' OR [Status]=''GI_POSTED'' OR [Status]=''GI_FAILED'' OR [Status]=''NEW'')';
DECLARE @ActualDefinition NVARCHAR(MAX);
SELECT @ActualDefinition = definition
FROM sys.check_constraints
WHERE parent_object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
  AND name = N'CK_INT_IntercompanyTransfer_Status'
  AND is_disabled = 0 AND is_not_trusted = 0;

IF @ActualDefinition IS NULL
    THROW 51001, 'Expected enabled/trusted status constraint was not found.', 1;

IF @ActualDefinition <> @OldDefinition
    THROW 51002, 'Status constraint changed since inspection; inspect before applying.', 1;

SELECT @Apply AS Applied, @ActualDefinition AS BeforeDefinition,
       N'Status IN (NEW, ERROR, GI_FAILED, GI_POSTED, GR_PENDING, COMPLETED)' AS ProposedAllowedStatuses,
       COUNT_BIG(*) AS ExistingRows
FROM dbo.INT_IntercompanyTransfer;

IF @Apply = 1
BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;
        IF NOT EXISTS
        (
            SELECT 1 FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
              AND name = N'CK_INT_IntercompanyTransfer_Status'
              AND definition = @OldDefinition
              AND is_disabled = 0 AND is_not_trusted = 0
        )
            THROW 51003, 'Constraint changed before apply; rolling back.', 1;

        ALTER TABLE dbo.INT_IntercompanyTransfer
            DROP CONSTRAINT CK_INT_IntercompanyTransfer_Status;
        ALTER TABLE dbo.INT_IntercompanyTransfer WITH CHECK
            ADD CONSTRAINT CK_INT_IntercompanyTransfer_Status
            CHECK (Status IN ('NEW', 'ERROR', 'GI_FAILED', 'GI_POSTED', 'GR_PENDING', 'COMPLETED'));
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT name, definition, is_disabled, is_not_trusted
    FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.INT_IntercompanyTransfer')
      AND name = N'CK_INT_IntercompanyTransfer_Status';
END;

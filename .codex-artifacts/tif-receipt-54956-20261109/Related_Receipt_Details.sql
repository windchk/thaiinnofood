SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS CheckedAt;
SELECT G.LogId,G.ProductionOrderDocEntry,G.Status,G.RequestDate,G.CompletedDate,
       G.SapDocumentEntry,G.SapDocumentNumber,G.ErrorMessage,
       JSON_VALUE(CASE WHEN ISJSON(G.RequestJson)=1 THEN G.RequestJson ELSE N'{}' END,'$.docDate') AS RequestDocDate,
       Q.RequestLines,Q.RequestQuantity
FROM TEMP_API.dbo.INT_SapApiLog G
OUTER APPLY
(
    SELECT COUNT(*) AS RequestLines,SUM(TRY_CONVERT(DECIMAL(19,6),JSON_VALUE(J.value,'$.quantity'))) AS RequestQuantity
    FROM OPENJSON(CASE WHEN ISJSON(G.RequestJson)=1 THEN G.RequestJson ELSE N'{}' END,'$.receiptLines') J
) Q
WHERE G.SapDatabaseName='TIF_GOLIVE' AND G.ProcessType='ReceiptFromProduction'
  AND G.ProductionOrderDocEntry IN(55014,55019,55027,54988)
ORDER BY G.ProductionOrderDocEntry,G.RequestDate;

SELECT L.BaseEntry AS ProductionOrderDocEntry,H.DocEntry,H.DocNum,H.CreateDate,H.DocTime,H.CANCELED,
       L.ItemCode,SUM(L.Quantity) AS ReceiptQuantity,H.UserSign,
       A.LogRows,A.SuccessLogId
FROM TIF_GOLIVE.dbo.OIGN H JOIN TIF_GOLIVE.dbo.IGN1 L ON L.DocEntry=H.DocEntry
OUTER APPLY
(
    SELECT COUNT(*) AS LogRows,MAX(CASE WHEN G.Status='S' THEN G.LogId END) AS SuccessLogId
    FROM TEMP_API.dbo.INT_SapApiLog G
    WHERE G.SapDatabaseName='TIF_GOLIVE' AND G.ProcessType='ReceiptFromProduction'
      AND TRY_CONVERT(INT,G.SapDocumentEntry)=H.DocEntry
) A
WHERE L.BaseType=202 AND L.BaseEntry IN(55014,55019,55027,54988)
GROUP BY L.BaseEntry,H.DocEntry,H.DocNum,H.CreateDate,H.DocTime,H.CANCELED,L.ItemCode,H.UserSign,A.LogRows,A.SuccessLogId
ORDER BY L.BaseEntry,H.DocEntry;

SELECT DocEntry,DocNum,ItemCode,PlannedQty,CmpltQty,RjctQty,Status
FROM TIF_GOLIVE.dbo.OWOR WHERE DocEntry IN(55014,55019,55027,54988) ORDER BY DocEntry;

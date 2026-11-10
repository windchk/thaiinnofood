SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS CheckedAt;
SELECT P.LogId AS ProcessingLogId,P.ProductionOrderDocEntry,P.RequestDate,
       DATEDIFF(SECOND,P.RequestDate,GETDATE()) AS ProcessingAgeSeconds,
       S.LogId AS LaterSuccessLogId,S.RequestDate AS LaterRequestDate,S.CompletedDate AS LaterCompletedDate,
       S.SapDocumentEntry,S.SapDocumentNumber,
       DATEDIFF(SECOND,P.RequestDate,S.RequestDate) AS SecondsUntilNextMatchingRequest,
       O.PlannedQty,O.CmpltQty,R.ReceiptDocuments,R.ReceiptQuantity,R.ReceiptsWithoutLog
FROM TEMP_API.dbo.INT_SapApiLog P
LEFT JOIN TIF_GOLIVE.dbo.OWOR O ON O.DocEntry=P.ProductionOrderDocEntry
OUTER APPLY
(
    SELECT TOP(1) G.LogId,G.RequestDate,G.CompletedDate,G.SapDocumentEntry,G.SapDocumentNumber
    FROM TEMP_API.dbo.INT_SapApiLog G
    WHERE G.SapDatabaseName=P.SapDatabaseName AND G.ProcessType=P.ProcessType
      AND G.ProductionOrderDocEntry=P.ProductionOrderDocEntry AND G.Status='S'
      AND G.RequestDate>P.RequestDate
      AND HASHBYTES('SHA2_256',G.RequestJson)=HASHBYTES('SHA2_256',P.RequestJson)
    ORDER BY G.RequestDate
) S
OUTER APPLY
(
    SELECT COUNT(*) AS ReceiptDocuments,SUM(D.Quantity) AS ReceiptQuantity,
           SUM(CASE WHEN D.HasLog=0 THEN 1 ELSE 0 END) AS ReceiptsWithoutLog
    FROM
    (
        SELECT H.DocEntry,SUM(L.Quantity) AS Quantity,
          CASE WHEN EXISTS(SELECT 1 FROM TEMP_API.dbo.INT_SapApiLog G
              WHERE G.SapDatabaseName='TIF_GOLIVE' AND G.ProcessType='ReceiptFromProduction'
                AND TRY_CONVERT(INT,G.SapDocumentEntry)=H.DocEntry) THEN 1 ELSE 0 END AS HasLog
        FROM TIF_GOLIVE.dbo.OIGN H JOIN TIF_GOLIVE.dbo.IGN1 L ON L.DocEntry=H.DocEntry
        WHERE L.BaseType=202 AND L.BaseEntry=P.ProductionOrderDocEntry AND H.CANCELED='N'
        GROUP BY H.DocEntry
    ) D
) R
WHERE P.SapDatabaseName='TIF_GOLIVE' AND P.ProcessType='ReceiptFromProduction' AND P.Status='P'
  AND P.RequestDate>=CONVERT(DATE,'20261109',112)
ORDER BY P.RequestDate;

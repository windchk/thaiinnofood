SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS CheckedAt,@@SERVERNAME AS ServerName;

SELECT LogId,SiteId,SapDatabaseName,ProcessType,ProductionOrderDocEntry,
       Status,ErrorMessage,SapDocumentEntry,SapDocumentNumber,RequestDate,CompletedDate,
       DATEDIFF(SECOND,RequestDate,COALESCE(CompletedDate,GETDATE())) AS AgeOrDurationSeconds,
       RequestJson,ResponseJson
FROM TEMP_API.dbo.INT_SapApiLog
WHERE SapDatabaseName='TIF_GOLIVE' AND ProductionOrderDocEntry=54956
ORDER BY LogId;

SELECT H.DocEntry,H.DocNum,H.DocDate,H.CreateDate,H.DocTime,H.CANCELED,H.UserSign,
       L.LineNum,L.BaseType,L.BaseEntry,L.BaseLine,L.ItemCode,L.Quantity,L.WhsCode,
       A.MatchingLogRows,A.SuccessLogRows,A.MatchingLogIds
FROM TIF_GOLIVE.dbo.OIGN H
JOIN TIF_GOLIVE.dbo.IGN1 L ON L.DocEntry=H.DocEntry
OUTER APPLY
(
    SELECT COUNT(*) AS MatchingLogRows,
       SUM(CASE WHEN G.Status='S' THEN 1 ELSE 0 END) AS SuccessLogRows,
       STUFF((SELECT ','+CONVERT(VARCHAR(20),G2.LogId)
              FROM TEMP_API.dbo.INT_SapApiLog G2
              WHERE G2.SapDatabaseName='TIF_GOLIVE' AND G2.ProcessType='ReceiptFromProduction'
                AND TRY_CONVERT(INT,G2.SapDocumentEntry)=H.DocEntry
              ORDER BY G2.LogId FOR XML PATH(''),TYPE).value('.','nvarchar(max)'),1,1,'') AS MatchingLogIds
    FROM TEMP_API.dbo.INT_SapApiLog G
    WHERE G.SapDatabaseName='TIF_GOLIVE' AND G.ProcessType='ReceiptFromProduction'
      AND TRY_CONVERT(INT,G.SapDocumentEntry)=H.DocEntry
) A
WHERE L.BaseType=202 AND L.BaseEntry=54956
ORDER BY H.DocEntry,L.LineNum;

SELECT H.DocEntry,H.DocNum,H.DocDate,H.CreateDate,H.DocTime,H.CANCELED,
       L.LineNum,L.BaseType,L.BaseEntry,L.BaseLine,L.ItemCode,L.Quantity,L.WhsCode
FROM TIF_GOLIVE.dbo.OIGE H JOIN TIF_GOLIVE.dbo.IGE1 L ON L.DocEntry=H.DocEntry
WHERE L.BaseType=202 AND L.BaseEntry=54956
ORDER BY H.DocEntry,L.LineNum;

SELECT DocEntry,DocNum,Status,ItemCode,PlannedQty,CmpltQty,RjctQty,PostDate,DueDate
FROM TIF_GOLIVE.dbo.OWOR WHERE DocEntry=54956;

SELECT S.session_id,S.host_name,S.program_name,S.status,S.login_time,S.last_request_start_time,
       S.last_request_end_time,S.open_transaction_count,R.status AS RequestStatus,
       R.command,R.wait_type,R.wait_time,R.blocking_session_id,R.total_elapsed_time
FROM sys.dm_exec_sessions S LEFT JOIN sys.dm_exec_requests R ON R.session_id=S.session_id
WHERE S.is_user_process=1 AND S.program_name LIKE '%Internet Information Services%'
ORDER BY S.session_id;

SELECT COUNT(*) AS AllProcessingRows,MIN(RequestDate) AS OldestProcessingRequest,
       SUM(CASE WHEN RequestDate>=CONVERT(DATE,'20261109',112) THEN 1 ELSE 0 END) AS ProcessingSinceNov09
FROM TEMP_API.dbo.INT_SapApiLog WHERE Status='P';

SELECT TOP(15) LogId,SiteId,SapDatabaseName,ProcessType,ProductionOrderDocEntry,Status,
       RequestDate,CompletedDate,DATEDIFF(SECOND,RequestDate,GETDATE()) AS AgeSeconds
FROM TEMP_API.dbo.INT_SapApiLog
WHERE SapDatabaseName='TIF_GOLIVE' AND Status='P'
ORDER BY RequestDate DESC;

-- Read-only monitor for the 1815-row PRD backfill committed on 2026-11-07 14:50.
USE [TEMP_API];
GO

SET LOCK_TIMEOUT 5000;
SELECT SYSDATETIMEOFFSET() AS CheckedAt;
WITH Responses AS
(
 SELECT Q.*,
 TRY_CONVERT(INT,JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.status')) AS OdooStatus,
 JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.results') AS OdooResults,
 JSON_QUERY(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.error') AS RpcError
 FROM dbo.INT_OdooQueue Q WHERE QueueId BETWEEN 1757 AND 3571
), Classified AS
(
 SELECT *,CASE
 WHEN Status='S' AND OdooStatus BETWEEN 200 AND 299 AND OdooResults='true' THEN 'ODOO_CONFIRMED'
 WHEN Status='S' AND (OdooStatus>=400 OR OdooResults='false' OR RpcError IS NOT NULL) THEN 'ODOO_REJECTED'
 WHEN Status='S' THEN 'S_UNVERIFIED'
 WHEN Status='E' THEN 'TRANSPORT_ERROR'
 ELSE Status END AS Result
 FROM Responses
)
SELECT SapDatabaseName,ObjectType,Result,COUNT_BIG(*) AS Documents,
MIN(ProcessDate) AS FirstProcessDate,MAX(ProcessDate) AS LastProcessDate
FROM Classified GROUP BY SapDatabaseName,ObjectType,Result ORDER BY ObjectType,SapDatabaseName,Result;
SELECT TOP(10) QueueId,ObjectType,ObjectKey,Status,RetryCount,CreateDate,ProcessDate,
JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.status') AS OdooStatus,
LEFT(COALESCE(JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.data'),
JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.error.message'),ErrorMessage),300) AS Message
FROM dbo.INT_OdooQueue WHERE QueueId BETWEEN 1757 AND 3571
AND (Status='E' OR (Status='S' AND
 (TRY_CONVERT(INT,JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.status'))>=400
 OR JSON_VALUE(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.result.results')='false'
 OR JSON_QUERY(CASE WHEN ISJSON(ResponseJson)=1 THEN ResponseJson ELSE N'{}' END,'$.error') IS NOT NULL)))
ORDER BY QueueId;

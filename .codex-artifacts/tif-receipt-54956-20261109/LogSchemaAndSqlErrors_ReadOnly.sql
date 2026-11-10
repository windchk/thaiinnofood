SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS CheckedAt;
SELECT message_id,language_id,severity,text FROM sys.messages WHERE message_id=17897 AND language_id=1033;
SELECT T.name AS TriggerName,CAST(T.is_disabled AS INT) AS IsDisabled,M.definition AS TriggerDefinition
FROM TEMP_API.sys.triggers T LEFT JOIN TEMP_API.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.parent_id=OBJECT_ID(N'TEMP_API.dbo.INT_SapApiLog',N'U');
SELECT I.name AS IndexName,CAST(I.is_unique AS INT) AS IsUnique,CAST(I.is_primary_key AS INT) AS IsPrimaryKey
FROM TEMP_API.sys.indexes I WHERE I.object_id=OBJECT_ID(N'TEMP_API.dbo.INT_SapApiLog',N'U') AND I.is_unique=1;
SELECT name AS CheckConstraint,definition FROM TEMP_API.sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'TEMP_API.dbo.INT_SapApiLog',N'U');
DECLARE @File NVARCHAR(4000);
SELECT @File=CAST(T.target_data AS XML).value('(EventFileTarget/File/@name)[1]','nvarchar(4000)')
FROM sys.dm_xe_sessions S JOIN sys.dm_xe_session_targets T ON T.event_session_address=S.address
WHERE S.name='system_health' AND T.target_name='event_file';
IF @File IS NOT NULL
BEGIN
    DECLARE @Pattern NVARCHAR(4000)=LEFT(@File,LEN(@File)-CHARINDEX(N'\',REVERSE(@File))+1)+N'system_health*.xel';
    SELECT DATEADD(HOUR,7,E.EventXml.value('(/event/@timestamp)[1]','datetime2')) AS EventTimeBangkok,
       E.EventXml.value('(/event/data[@name="message"]/value/text())[1]','nvarchar(2000)') AS ErrorMessage,
       E.EventXml.value('(/event/action[@name="client_app_name"]/value/text())[1]','nvarchar(256)') AS ClientApp,
       E.EventXml.value('(/event/action[@name="client_hostname"]/value/text())[1]','nvarchar(256)') AS ClientHost,
       E.EventXml.value('(/event/action[@name="session_id"]/value/text())[1]','nvarchar(30)') AS SessionId
    FROM sys.fn_xe_file_target_read_file(@Pattern,NULL,NULL,NULL) X
    CROSS APPLY(SELECT CAST(X.event_data AS XML) AS EventXml) E
    WHERE X.object_name=N'error_reported'
      AND E.EventXml.value('(/event/@timestamp)[1]','datetime2')>='2026-11-09T05:30:00'
      AND E.EventXml.value('(/event/@timestamp)[1]','datetime2')<='2026-11-09T12:45:00'
    ORDER BY EventTimeBangkok;
END;

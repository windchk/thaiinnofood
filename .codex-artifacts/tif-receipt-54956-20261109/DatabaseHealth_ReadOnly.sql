SET NOCOUNT ON;
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS CheckedAt,SYSDATETIMEOFFSET() AS ServerTimeWithOffset;
DECLARE @File NVARCHAR(4000);
SELECT @File=CAST(T.target_data AS XML).value('(EventFileTarget/File/@name)[1]','nvarchar(4000)')
FROM sys.dm_xe_sessions S JOIN sys.dm_xe_session_targets T ON T.event_session_address=S.address
WHERE S.name='system_health' AND T.target_name='event_file';
IF @File IS NULL
BEGIN
    SELECT N'system_health event file is unavailable' AS InspectionStatus;
END
ELSE
BEGIN
    DECLARE @Pattern NVARCHAR(4000)=LEFT(@File,LEN(@File)-CHARINDEX(N'\',REVERSE(@File))+1)+N'system_health*.xel';
    DECLARE @Events TABLE(EventUtc DATETIME2,ObjectName NVARCHAR(256),ErrorNumber INT,Severity INT,
                         MentionsApiLog BIT,MentionsReceipt BIT,ClientApp NVARCHAR(256));
    INSERT INTO @Events
    SELECT E.EventXml.value('(/event/@timestamp)[1]','datetime2'),X.object_name,
       TRY_CONVERT(INT,E.EventXml.value('(/event/data[@name="error_number"]/value/text())[1]','nvarchar(30)')),
       TRY_CONVERT(INT,E.EventXml.value('(/event/data[@name="severity"]/value/text())[1]','nvarchar(30)')),
       CASE WHEN X.event_data LIKE N'%INT_SapApiLog%' THEN 1 ELSE 0 END,
       CASE WHEN X.event_data LIKE N'%OIGN%' OR X.event_data LIKE N'%IGN1%' THEN 1 ELSE 0 END,
       E.EventXml.value('(/event/data/value/deadlock/process-list/process/@clientapp)[1]','nvarchar(256)')
    FROM sys.fn_xe_file_target_read_file(@Pattern,NULL,NULL,NULL) X
    CROSS APPLY(SELECT CAST(X.event_data AS XML) AS EventXml) E;

    SELECT DATEADD(HOUR,7,MIN(EventUtc)) AS EarliestRetainedEventBangkok,
           DATEADD(HOUR,7,MAX(EventUtc)) AS LatestRetainedEventBangkok,COUNT(*) AS RetainedEvents
    FROM @Events;
    SELECT ObjectName,COUNT(*) AS EventsInIncidentWindow,
           SUM(CONVERT(INT,MentionsApiLog)) AS EventsMentioningApiLog,
           SUM(CONVERT(INT,MentionsReceipt)) AS EventsMentioningReceipt
    FROM @Events
    WHERE EventUtc>='2026-11-09T05:30:00' AND EventUtc<='2026-11-09T12:45:00'
      AND ObjectName IN(N'xml_deadlock_report',N'error_reported',N'connectivity_ring_buffer_recorded')
    GROUP BY ObjectName;
    SELECT TOP(40) DATEADD(HOUR,7,EventUtc) AS EventTimeBangkok,ObjectName,ErrorNumber,Severity,
           MentionsApiLog,MentionsReceipt,ClientApp
    FROM @Events
    WHERE EventUtc>='2026-11-09T05:30:00' AND EventUtc<='2026-11-09T12:45:00'
      AND ObjectName IN(N'xml_deadlock_report',N'error_reported',N'connectivity_ring_buffer_recorded')
    ORDER BY EventUtc;
END;

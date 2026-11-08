param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$cfg = Get-Content -LiteralPath (Join-Path $projectRoot 'OdooSyncWorker\appsettings.json') -Raw | ConvertFrom-Json
$builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$cfg.ConnectionStrings.TempApiDb)
$builder['Connect Timeout'] = 5
$builder['Application Name'] = 'TIF ReadOnly Delivery Date Audit'
$connection = [System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandTimeout = 30
    $command.CommandText = @'
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS ServerCheckedAt, @@SERVERNAME AS ServerName;
;WITH Q AS (
    SELECT Q.*,
      CASE
        WHEN Q.SapDatabaseName IN ('TIF_GOLIVE','SBO_STL_GOLIVE') THEN Q.SapDatabaseName
        WHEN Q.SiteId='PRD-TIF' OR (Q.SiteId='PRD' AND Q.CompanyName='TIF') THEN 'TIF_GOLIVE'
        WHEN Q.SiteId='PRD-STL' OR (Q.SiteId='PRD' AND Q.CompanyName='STL') THEN 'SBO_STL_GOLIVE'
      END AS ResolvedDatabase,
      CASE WHEN ISJSON(Q.ResponseJson)=1 THEN Q.ResponseJson ELSE N'{}' END AS SafeResponse,
      CASE WHEN ISJSON(Q.RequestJson)=1 THEN Q.RequestJson ELSE N'{}' END AS SafeRequest
    FROM TEMP_API.dbo.INT_OdooQueue Q
    WHERE Q.ObjectType IN ('ARReserveInvoice','ProductionOrder')
      AND (Q.SapDatabaseName IN ('TIF_GOLIVE','SBO_STL_GOLIVE') OR Q.SiteId IN ('PRD','PRD-TIF','PRD-STL'))
), Documents AS (
    SELECT 'TIF_GOLIVE' AS DatabaseName,'ARReserveInvoice' AS ObjectType,
      DocEntry,DocNum,DocDate AS PostingDate,DocDueDate AS DueDate,DocStatus AS SourceStatus,
      CANCELED AS SourceCancelled,isIns AS ReserveInvoice
    FROM TIF_GOLIVE.dbo.OINV
    UNION ALL
    SELECT 'SBO_STL_GOLIVE','ARReserveInvoice',DocEntry,DocNum,DocDate,DocDueDate,DocStatus,CANCELED,isIns
    FROM SBO_STL_GOLIVE.dbo.OINV
    UNION ALL
    SELECT 'TIF_GOLIVE','ProductionOrder',DocEntry,DocNum,PostDate,DueDate,Status,NULL,NULL
    FROM TIF_GOLIVE.dbo.OWOR
)
SELECT Q.QueueId,Q.ResolvedDatabase,Q.ObjectType,Q.ObjectKey,Q.ActionType,Q.Status,Q.RetryCount,
 Q.CreateDate,Q.ProcessDate,Q.ProcessBy,D.DocEntry,D.DocNum,D.PostingDate,D.DueDate,
 D.SourceStatus,D.SourceCancelled,D.ReserveInvoice,
 TRY_CONVERT(INT,JSON_VALUE(Q.SafeResponse,'$.result.status')) AS OdooBusinessStatus,
 JSON_VALUE(Q.SafeResponse,'$.result.results') AS OdooResults,
 CAST(CASE WHEN TRY_CONVERT(INT,JSON_VALUE(Q.SafeResponse,'$.result.status')) BETWEEN 200 AND 299
        AND JSON_VALUE(Q.SafeResponse,'$.result.results')='true' THEN 1 ELSE 0 END AS BIT) AS OdooConfirmed,
 COALESCE(JSON_VALUE(Q.SafeRequest,'$.params.deliveries[0].companyName'),JSON_VALUE(Q.SafeRequest,'$.params.productions[0].companyName')) AS PayloadDatabase,
 COALESCE(JSON_VALUE(Q.SafeRequest,'$.params.deliveries[0].docEntry'),JSON_VALUE(Q.SafeRequest,'$.params.productions[0].docEntry')) AS PayloadDocEntry
FROM Q LEFT JOIN Documents D ON D.DatabaseName=Q.ResolvedDatabase
 AND D.ObjectType=Q.ObjectType AND D.DocEntry=TRY_CONVERT(INT,Q.ObjectKey)
ORDER BY Q.QueueId;
SELECT 'TIF_GOLIVE' AS DatabaseName,T.name AS TriggerName,CAST(T.is_disabled AS INT) AS IsDisabled,
 CASE WHEN CHARINDEX(N'I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)',M.definition)>0 THEN 'DocDueDate >= 2026-11-09'
      WHEN CHARINDEX(N'I.DueDate >= CONVERT(DATE, ''20261109'', 112)',M.definition)>0 THEN 'DueDate >= 2026-11-09'
      ELSE 'CHECK DEFINITION' END AS ActualDateFilter
FROM TIF_GOLIVE.sys.triggers T JOIN TIF_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.name IN ('TRG_OINV_Reserve_ToOdooQueue','TRG_OWOR_ToOdooQueue')
UNION ALL
SELECT 'SBO_STL_GOLIVE',T.name,CAST(T.is_disabled AS INT),
 CASE WHEN CHARINDEX(N'I.DocDueDate >= CONVERT(DATE, ''20261109'', 112)',M.definition)>0 THEN 'DocDueDate >= 2026-11-09'
      ELSE 'CHECK DEFINITION' END
FROM SBO_STL_GOLIVE.sys.triggers T JOIN SBO_STL_GOLIVE.sys.sql_modules M ON M.object_id=T.object_id
WHERE T.name='TRG_OINV_Reserve_ToOdooQueue';
'@
    $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($command)
    $data = [System.Data.DataSet]::new()
    [void]$adapter.Fill($data)
} finally { $connection.Dispose() }

function Convert-Row($row) {
    $o = [ordered]@{}
    foreach ($col in $row.Table.Columns) {
        $v = $row[$col.ColumnName]
        if ($v -is [DBNull]) { $v = $null }
        elseif ($v -is [datetime]) { $v = $v.ToString('yyyy-MM-dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture) }
        $o[$col.ColumnName] = $v
    }
    [pscustomobject]$o
}
$rows = @($data.Tables[1].Rows | ForEach-Object { Convert-Row $_ })
$cutoff = '2026-11-09 00:00:00.000'
$today = '2026-11-07 00:00:00.000'
$tomorrow = '2026-11-08 00:00:00.000'
$cutover = '2026-11-07 14:50:09.470'
foreach ($row in $rows) {
    $scope = if ($row.QueueId -ge 1757 -and $row.QueueId -le 3571) { 'Backfill1757-3571' }
        elseif ($row.CreateDate -ge $cutover) { 'AfterCutoverLive' }
        elseif ($row.CreateDate -ge $today -or ($row.ProcessDate -ge $today -and $row.ProcessDate -lt $tomorrow)) { 'BeforeCutoverToday' }
        else { 'EarlierHistory' }
    $row | Add-Member -NotePropertyName AuditScope -NotePropertyValue $scope
}
$active = @($rows | Where-Object { $_.AuditScope -ne 'EarlierHistory' })
$confirmed = @($active | Where-Object { $_.OdooConfirmed })
$wrong = @($confirmed | Where-Object { $_.ObjectType -eq 'ARReserveInvoice' -and $_.PostingDate -and $_.PostingDate -lt $cutoff })
$production = @($confirmed | Where-Object { $_.ObjectType -eq 'ProductionOrder' })
$summary = [ordered]@{
    CheckedAt = (Convert-Row $data.Tables[0].Rows[0]).ServerCheckedAt
    CriterionUnderReview = 'ARReserveInvoice SAP DocDate < 2026-11-09, compared with date visible in Odoo screenshot'
    CurrentRule = 'AR DocDueDate >= 2026-11-09; Production DueDate >= 2026-11-09'
    TodayQueueRows = $active.Count
    TodayConfirmedRows = $confirmed.Count
    TodayPendingRows = @($active | Where-Object Status -in 'N','P').Count
    TodayNonConfirmedRows = @($active | Where-Object { -not $_.OdooConfirmed }).Count
    SourceMissingRows = @($active | Where-Object { $null -eq $_.DocEntry }).Count
    PayloadDatabaseMismatchRows = @($confirmed | Where-Object { $_.PayloadDatabase -ne $_.ResolvedDatabase }).Count
    PayloadDocEntryMismatchRows = @($confirmed | Where-Object { $_.PayloadDocEntry -ne [string]$_.DocEntry }).Count
    ConfirmedArPostingBeforeCutoffRows = $wrong.Count
    ConfirmedArPostingBeforeCutoffDistinctDocuments = @($wrong | ForEach-Object { "$($_.ResolvedDatabase)|$($_.DocEntry)" } | Sort-Object -Unique).Count
    ConfirmedArDueDateBeforeCutoffRows = @($confirmed | Where-Object { $_.ObjectType -eq 'ARReserveInvoice' -and $_.DueDate -lt $cutoff }).Count
    ConfirmedProductionRows = $production.Count
    ConfirmedProductionDueDateBeforeCutoffRows = @($production | Where-Object { $_.DueDate -lt $cutoff }).Count
    TodayGroups = @($active | Group-Object AuditScope,ResolvedDatabase,ObjectType | ForEach-Object {
        $g = @($_.Group); $ok = @($g | Where-Object OdooConfirmed)
        $bad = @($ok | Where-Object { $_.ObjectType -eq 'ARReserveInvoice' -and $_.PostingDate -lt $cutoff })
        [pscustomobject]@{ Scope=$g[0].AuditScope;Database=$g[0].ResolvedDatabase;ObjectType=$g[0].ObjectType;QueueRows=$g.Count;ConfirmedRows=$ok.Count;PostingBeforeCutoff=$bad.Count;FirstResult=($ok.ProcessDate|Sort-Object|Select-Object -First 1);LastResult=($ok.ProcessDate|Sort-Object|Select-Object -Last 1) }
    })
    PostingBeforeCutoffGroups = @($wrong | Group-Object AuditScope,ResolvedDatabase | ForEach-Object {
        $g = @($_.Group)
        [pscustomobject]@{Scope=$g[0].AuditScope;Database=$g[0].ResolvedDatabase;Rows=$g.Count;PostingNov08=@($g|Where-Object{$_.PostingDate -ge '2026-11-08 00:00:00.000'}).Count;PostingEarlierThanNov08=@($g|Where-Object{$_.PostingDate -lt '2026-11-08 00:00:00.000'}).Count;FirstPosting=($g.PostingDate|Sort-Object|Select-Object -First 1);LastPosting=($g.PostingDate|Sort-Object|Select-Object -Last 1);FirstResult=($g.ProcessDate|Sort-Object|Select-Object -First 1);LastResult=($g.ProcessDate|Sort-Object|Select-Object -Last 1);FirstQueue=($g.QueueId|Measure-Object -Minimum).Minimum;LastQueue=($g.QueueId|Measure-Object -Maximum).Maximum}
    })
    CurrentTriggers = @($data.Tables[2].Rows | ForEach-Object { Convert-Row $_ })
    HistoricalStatusSWithoutConfirmedResponse = @($rows | Where-Object { $_.AuditScope -eq 'EarlierHistory' -and $_.Status -eq 'S' -and -not $_.OdooConfirmed } | Group-Object ResolvedDatabase,ObjectType,OdooBusinessStatus | ForEach-Object { [pscustomobject]@{Group=$_.Name;Rows=$_.Count} })
    PostingBeforeCutoffExamples = @($wrong | Sort-Object QueueId | Select-Object -First 5 QueueId,ResolvedDatabase,DocEntry,DocNum,PostingDate,DueDate,ProcessDate)
    StlPostingBeforeCutoff = @($wrong | Where-Object ResolvedDatabase -eq 'SBO_STL_GOLIVE' | Select-Object QueueId,DocEntry,DocNum,PostingDate,DueDate,ProcessDate)
}
$wrong | Select-Object QueueId,ResolvedDatabase,ObjectType,DocEntry,DocNum,PostingDate,DueDate,CreateDate,ProcessDate,AuditScope,Status,OdooBusinessStatus,OdooResults,RetryCount,SourceStatus,SourceCancelled |
    Export-Csv -LiteralPath (Join-Path $PSScriptRoot 'AR_PostingDate_Before_20261109_OdooConfirmed.csv') -NoTypeInformation -Encoding utf8
$active | Export-Csv -LiteralPath (Join-Path $PSScriptRoot 'Today_PRD_Queue_Audit.csv') -NoTypeInformation -Encoding utf8
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'DeliveryDate_Audit_Summary.json') -Encoding utf8
$summary | ConvertTo-Json -Depth 8

$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$cfg=Get-Content -LiteralPath (Join-Path $root 'OdooSyncWorker\appsettings.json') -Raw|ConvertFrom-Json
$b=[System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$cfg.ConnectionStrings.TempApiDb)
$b['Data Source']='tcp:192.168.10.1,1433'
$b['Connect Timeout']=5
$b['ConnectRetryCount']=0
$b['Application Name']='TIF ReadOnly Receipt 54956 Audit'
$cn=[System.Data.SqlClient.SqlConnection]::new($b.ConnectionString)
try{
    $cn.Open()
    $cmd=$cn.CreateCommand()
    $cmd.CommandTimeout=25
    $cmd.CommandText=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Inspect_ReadOnly.sql') -Raw
    $adapter=[System.Data.SqlClient.SqlDataAdapter]::new($cmd)
    $dataset=[System.Data.DataSet]::new()
    [void]$adapter.Fill($dataset)
    $names=@('CheckedAt','ProductionLogs','SapReceipts','SapIssues','ProductionOrder','IisSessions','ProcessingSummary','RecentProcessingRows')
    $result=[ordered]@{}
    for($i=0;$i -lt $dataset.Tables.Count;$i++){
        $table=$dataset.Tables[$i]
        $rows=@(foreach($row in $table.Rows){
            $o=[ordered]@{}
            foreach($column in $table.Columns){
                $v=$row[$column.ColumnName]
                if($v -is [DBNull]){$v=$null}
                elseif($v -is [datetime]){$v=$v.ToString('yyyy-MM-dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture)}
                $o[$column.ColumnName]=$v
            }
            [pscustomobject]$o
        })
        $result[$names[$i]]=$rows
    }
    $result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'Inspection.json') -Encoding utf8
    $result.ProductionLogs=@($result.ProductionLogs|Select-Object LogId,ProcessType,Status,RequestDate,CompletedDate,AgeOrDurationSeconds,SapDocumentEntry,SapDocumentNumber,ErrorMessage)
    $result|ConvertTo-Json -Depth 8
}finally{$cn.Dispose()}

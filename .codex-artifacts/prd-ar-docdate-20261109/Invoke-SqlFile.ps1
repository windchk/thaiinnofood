param(
    [Parameter(Mandatory=$true)][string]$SqlFile,
    [switch]$Apply,
    [string]$ResultFile
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$config=Get-Content -LiteralPath (Join-Path $projectRoot 'OdooSyncWorker\appsettings.json') -Raw | ConvertFrom-Json
$builder=[System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$config.ConnectionStrings.TempApiDb)
$builder['Data Source']='tcp:192.168.10.1,1433'
$builder['Connect Timeout']=5
$builder['ConnectRetryCount']=0
$builder['Application Name']=if($Apply){'TIF Authorized AR DocDate Apply'}else{'TIF AR DocDate Preview-Test'}
$sql=Get-Content -LiteralPath $SqlFile -Raw
if($Apply){
    $pattern='(?m)^DECLARE @Apply BIT = 0;\r?$'
    if([regex]::Matches($sql,$pattern).Count -ne 1){throw 'Expected one preview-mode declaration.'}
    $sql=[regex]::Replace($sql,$pattern,'DECLARE @Apply BIT = 1;')
}
$connection=[System.Data.SqlClient.SqlConnection]::new($builder.ConnectionString)
$results=[System.Collections.Generic.List[object]]::new()
try{
    $connection.Open()
    foreach($batch in [regex]::Split($sql,'(?im)^[\t ]*GO[\t ]*(?:--[^\r\n]*)?\r?$')){
        if([string]::IsNullOrWhiteSpace($batch)){continue}
        $command=$connection.CreateCommand()
        $command.CommandText=$batch
        $command.CommandTimeout=30
        $adapter=[System.Data.SqlClient.SqlDataAdapter]::new($command)
        $dataset=[System.Data.DataSet]::new()
        [void]$adapter.Fill($dataset)
        foreach($table in $dataset.Tables){
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
            $results.Add([pscustomobject]@{ResultSet=$results.Count;Rows=$rows})
        }
    }
}finally{$connection.Dispose()}
$result=[ordered]@{Applied=[bool]$Apply;SqlFile=[System.IO.Path]::GetFullPath($SqlFile);Results=@($results.ToArray())}
$json=$result|ConvertTo-Json -Depth 10
if($ResultFile){$json|Set-Content -LiteralPath $ResultFile -Encoding utf8}
$json

param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$settings = Get-Content -LiteralPath (Join-Path $workspace 'OdooSyncWorker\appsettings.json') -Raw | ConvertFrom-Json
$connectionSettings = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$settings.ConnectionStrings.TempApiDb)
$connectionSettings['Data Source'] = 'tcp:192.168.10.1,1433'
$connectionSettings['Connect Timeout'] = 5
$connectionSettings['ConnectRetryCount'] = 0
$connectionSettings['Application Name'] = 'TIF authorized w3wp full crash dump setup'
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionSettings.ConnectionString)
$targetKey = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\w3wp.exe'
$dumpValues = @(
    [pscustomobject]@{ Name = 'DumpCount'; Type = 'REG_DWORD'; Value = 3 }
    [pscustomobject]@{ Name = 'DumpType'; Type = 'REG_DWORD'; Value = 2 }
)
$evidence = [ordered]@{
    StartedAtBangkok = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([datetime]::UtcNow, 'SE Asia Standard Time').ToString('yyyy-MM-dd HH:mm:ss zzz', [Globalization.CultureInfo]::InvariantCulture)
    Applied = $false
    TargetKey = 'HKEY_LOCAL_MACHINE\' + $targetKey
    DumpFolder = 'Windows default: %LOCALAPPDATA%\CrashDumps in the crashing process account profile; no custom ACL changes.'
    IntendedValues = $dumpValues
    Before = @()
    WritesVerified = @()
    Failure = $null
}

function Read-DumpValue([string]$ValueName) {
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 10
        $command.CommandText = 'EXEC master.sys.xp_regread @rootkey, @key, @value_name;'
        [void]$command.Parameters.AddWithValue('@rootkey', 'HKEY_LOCAL_MACHINE')
        [void]$command.Parameters.AddWithValue('@key', $targetKey)
        [void]$command.Parameters.AddWithValue('@value_name', $ValueName)
        $table = [System.Data.DataTable]::new()
        $reader = $command.ExecuteReader()
        try { $table.Load($reader) } finally { $reader.Dispose() }
        $data = if ($table.Rows.Count -gt 0 -and $table.Columns.Count -gt 1) { $table.Rows[0][1] } else { $null }
        [pscustomobject]@{ Name = $ValueName; Present = ($table.Rows.Count -gt 0); Value = $data }
    } finally { $command.Dispose() }
}

try {
    $connection.Open()
    $evidence.Before = @($dumpValues | ForEach-Object { Read-DumpValue $_.Name })
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'FullDumpSetup-Before.json') -Encoding UTF8
    if ($Apply) {
        foreach ($dumpValue in $dumpValues) {
            $command = $connection.CreateCommand()
            try {
                $command.CommandTimeout = 10
                $command.CommandText = 'EXEC master.sys.xp_regwrite @rootkey, @key, @value_name, @type, @value;'
                [void]$command.Parameters.AddWithValue('@rootkey', 'HKEY_LOCAL_MACHINE')
                [void]$command.Parameters.AddWithValue('@key', $targetKey)
                [void]$command.Parameters.AddWithValue('@value_name', $dumpValue.Name)
                [void]$command.Parameters.AddWithValue('@type', $dumpValue.Type)
                [void]$command.Parameters.AddWithValue('@value', $dumpValue.Value)
                [void]$command.ExecuteNonQuery()
            } finally { $command.Dispose() }
            $actualValue = Read-DumpValue $dumpValue.Name
            if (-not $actualValue.Present -or [string]$actualValue.Value -ne [string]$dumpValue.Value) {
                throw "Registry write was not verified: $($dumpValue.Name)."
            }
            $evidence.WritesVerified += $actualValue
        }
        $evidence.Applied = $true
    }
} catch {
    $evidence.Failure = $_.Exception.Message
} finally {
    $connection.Dispose()
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'FullDumpSetup-Result.json') -Encoding UTF8
}
$evidence | ConvertTo-Json -Depth 7
if ($evidence.Failure) { exit 1 }

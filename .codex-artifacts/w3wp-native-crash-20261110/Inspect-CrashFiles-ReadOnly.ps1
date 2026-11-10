$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$workerSettings = Get-Content -LiteralPath (Join-Path $workspace 'OdooSyncWorker\appsettings.json') -Raw | ConvertFrom-Json
$connectionSettings = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$workerSettings.ConnectionStrings.TempApiDb)
$connectionSettings['Data Source'] = 'tcp:192.168.10.1,1433'
$connectionSettings['Connect Timeout'] = 5
$connectionSettings['ConnectRetryCount'] = 0
$connectionSettings['Application Name'] = 'TIF read-only crash file inventory'
$connection = [System.Data.SqlClient.SqlConnection]::new($connectionSettings.ConnectionString)

function Read-ServerDirectory([string]$Directory, [int]$Depth = 0) {
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 15
        $command.CommandText = 'EXEC master.sys.xp_dirtree @directory, @depth, 1;'
        [void]$command.Parameters.Add('@directory', [System.Data.SqlDbType]::NVarChar, 512)
        [void]$command.Parameters.Add('@depth', [System.Data.SqlDbType]::Int)
        $command.Parameters['@directory'].Value = $Directory
        $command.Parameters['@depth'].Value = $Depth
        $table = [System.Data.DataTable]::new()
        $reader = $command.ExecuteReader()
        try { $table.Load($reader) } finally { $reader.Dispose() }
        $parents = @{ 0 = $Directory }
        $entries = @(
            foreach ($row in $table.Rows) {
                $level = [int]$row.depth
                $entryPath = if ($parents.ContainsKey($level - 1)) {
                    Join-Path $parents[$level - 1] ([string]$row.subdirectory)
                } else { [string]$row.subdirectory }
                if ([int]$row.file -eq 0) { $parents[$level] = $entryPath }
                [pscustomobject]@{ Path = $entryPath; IsFile = ([int]$row.file -eq 1) }
            }
        )
        [pscustomobject]@{ Root = $Directory; Entries = $entries; Error = $null }
    } catch {
        [pscustomobject]@{ Root = $Directory; Entries = @(); Error = $_.Exception.Message }
    } finally { $command.Dispose() }
}

function Read-ServerRegistryValue([string]$Key, [string]$ValueName) {
    $command = $connection.CreateCommand()
    try {
        $command.CommandTimeout = 10
        $command.CommandText = 'EXEC master.sys.xp_regread @rootkey, @key, @value_name;'
        [void]$command.Parameters.Add('@rootkey', [System.Data.SqlDbType]::NVarChar, 30)
        [void]$command.Parameters.Add('@key', [System.Data.SqlDbType]::NVarChar, 512)
        [void]$command.Parameters.Add('@value_name', [System.Data.SqlDbType]::NVarChar, 128)
        $command.Parameters['@rootkey'].Value = 'HKEY_LOCAL_MACHINE'
        $command.Parameters['@key'].Value = $Key
        $command.Parameters['@value_name'].Value = $ValueName
        $table = [System.Data.DataTable]::new()
        $reader = $command.ExecuteReader()
        try { $table.Load($reader) } finally { $reader.Dispose() }
        $value = if ($table.Rows.Count -gt 0 -and $table.Columns.Count -gt 1) { $table.Rows[0][1] } else { $null }
        [pscustomobject]@{ Key = $Key; ValueName = $ValueName; Value = $value; Error = $null }
    } catch {
        [pscustomobject]@{ Key = $Key; ValueName = $ValueName; Value = $null; Error = $_.Exception.Message }
    } finally { $command.Dispose() }
}

try {
    $connection.Open()
    $registry = @(
        foreach ($key in @(
            'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps',
            'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\w3wp.exe'
        )) {
            foreach ($valueName in @('DumpFolder', 'DumpType', 'DumpCount')) {
                Read-ServerRegistryValue $key $valueName
            }
        }
        foreach ($key in @(
            'SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug',
            'SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AeDebug'
        )) {
            foreach ($valueName in @('Auto', 'Debugger')) {
                Read-ServerRegistryValue $key $valueName
            }
        }
    )
    $profiles = Read-ServerDirectory 'C:\Users' 1
    $roots = @(
        'C:\ProgramData\Microsoft\Windows\WER'
        'C:\Windows\System32\config\systemprofile\AppData\Local\CrashDumps'
        'C:\Windows\ServiceProfiles\LocalService\AppData\Local\CrashDumps'
        'C:\Windows\ServiceProfiles\NetworkService\AppData\Local\CrashDumps'
        'C:\Dumps'
        'D:\Dumps'
        'C:\CrashDumps'
        'D:\CrashDumps'
        $profiles.Entries | Where-Object { -not $_.IsFile } | ForEach-Object { Join-Path $_.Path 'AppData\Local\CrashDumps' }
        $registry | Where-Object { $_.ValueName -eq 'DumpFolder' -and $_.Value -is [string] -and $_.Value -notmatch '%' } | ForEach-Object { $_.Value }
    ) | Select-Object -Unique
    $inventory = @(
        foreach ($root in $roots) {
            $listing = Read-ServerDirectory $root
            [pscustomobject]@{
                Root = $root
                EntriesObserved = $listing.Entries.Count
                WerReportCount = @($listing.Entries | Where-Object { $_.IsFile -and $_.Path -match '\.wer$' }).Count
                DumpOrCabMatches = @($listing.Entries | Where-Object { $_.IsFile -and $_.Path -match '\.(dmp|hdmp|mdmp|cab)$' } | Select-Object -ExpandProperty Path)
                Error = $listing.Error
                EmptyResultLimitation = if ($listing.Entries.Count -eq 0 -and -not $listing.Error) { 'No entries visible to the SQL Server service identity; missing and inaccessible directories are not distinguished.' } else { $null }
            }
        }
    )
    $report = [pscustomobject]@{
        CheckedAtBangkok = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([datetime]::UtcNow, 'SE Asia Standard Time').ToString('yyyy-MM-dd HH:mm:ss zzz', [Globalization.CultureInfo]::InvariantCulture)
        Mode = 'Read-only directory and registry queries through existing authorized SQL connection; no SAP calls, no SQL application data updates, no registry writes.'
        Registry = $registry
        ProfileRootReadError = $profiles.Error
        Directories = $inventory
        DumpOrCabMatchCount = @($inventory | ForEach-Object { $_.DumpOrCabMatches }).Count
    }
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'ServerCrashFileInventory.json') -Encoding UTF8
    [pscustomobject]@{
        CheckedAtBangkok = $report.CheckedAtBangkok
        DirectoriesChecked = $inventory.Count
        WerReports = ($inventory | Measure-Object WerReportCount -Sum).Sum
        DumpOrCabMatchCount = $report.DumpOrCabMatchCount
        Registry = $registry
        ReadErrors = @($inventory | Where-Object Error | Select-Object Root,Error)
    } | ConvertTo-Json -Depth 6
} finally { $connection.Dispose() }

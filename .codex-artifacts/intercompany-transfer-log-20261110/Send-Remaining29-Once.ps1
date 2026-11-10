param([switch]$Execute, [string]$ResumeAfterJournal, [switch]$ContinueOnStockError)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$baseUrl = 'http://192.168.10.1:8585/api/sap/goodsissue-goodsreceipt'
$excludedId = 'DO26-02023-2'
$originalScope = @(Import-Csv -LiteralPath (Join-Path $PSScriptRoot 'PRD_30_NEW.csv'))
if ($originalScope.Count -ne 30 -or @($originalScope | Where-Object SiteId -NE 'PRD-TIF').Count -ne 0) {
    throw 'Expected the original 30 PRD-TIF transfers.'
}
$allowedIds = @($originalScope.TransferId | Where-Object { $_ -ne $excludedId })
if ($allowedIds.Count -ne 29 -or @($allowedIds | Sort-Object -Unique).Count -ne 29) {
    throw 'Expected exactly 29 distinct remaining TransferIds.'
}
$previouslyAttemptedIds = @()
if ($ResumeAfterJournal) {
    $previous = Get-Content -LiteralPath $ResumeAfterJournal -Raw | ConvertFrom-Json
    $previouslyAttemptedIds = @($previous.Attempts.TransferId)
    if ($previous.Plan.Endpoint -ne $baseUrl -or $previouslyAttemptedIds.Count -eq 0 -or
        @($previouslyAttemptedIds | Where-Object { $_ -notin $allowedIds }).Count -gt 0 -or
        @($previouslyAttemptedIds | Sort-Object -Unique).Count -ne $previouslyAttemptedIds.Count) {
        throw 'Previous attempt journal does not match this authorized scope.'
    }
}

$originalFile = 'C:\Users\User\.codex\attachments\cf98350c-3220-4604-9d6d-b73484097368\Pasted text.txt'
if ((Get-FileHash -LiteralPath $originalFile -Algorithm SHA256).Hash -ne 'A77334DD8F516052A82FCD9D1078C63356CD6BFA4C2B5B33DF39DFE9855F93B5') {
    throw 'Original user-supplied payload evidence has changed.'
}
$originalRequests = @{}
foreach ($line in [IO.File]::ReadLines($originalFile)) {
    if ($line.StartsWith("PRD-TIF`t")) {
        $fields = $line -split "`t",18
        $originalRequests[$fields[1]] = [pscustomobject]@{Hash=$fields[5];Json=$fields[6]}
    }
}

$config = Get-Content -LiteralPath (Join-Path $repoRoot 'OdooSyncWorker\appsettings.json') -Raw | ConvertFrom-Json
$db = [System.Data.SqlClient.SqlConnectionStringBuilder]::new([string]$config.ConnectionStrings.TempApiDb)
$db['Data Source'] = 'tcp:192.168.10.1,1433'
$db['Connect Timeout'] = 5
$db['ConnectRetryCount'] = 0
$db['Application Name'] = 'TIF Authorized Remaining29 Readback'

function Get-Hash([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value)))
}

function Get-Snapshot([string[]]$TransferIds) {
    $connection = [System.Data.SqlClient.SqlConnection]::new($db.ConnectionString)
    try {
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 20
        $values = @(for ($i=0; $i -lt $TransferIds.Count; $i++) {
            $id = $TransferIds[$i]
            $t = '@T'+$i
            $r = '@R'+$i
            $null = $command.Parameters.AddWithValue($t,$id)
            $null = $command.Parameters.AddWithValue($r,(Get-Hash $id).Substring(0,11))
            '('+$t+','+$r+')'
        })
        $command.CommandText = @'
SET LOCK_TIMEOUT 5000;
SELECT GETDATE() AS ServerLocalTime,GETUTCDATE() AS ServerUtcTime;
WITH Requested(TransferId,ShortReference) AS (SELECT TransferId,ShortReference FROM (VALUES /*SCOPE*/) V(TransferId,ShortReference))
SELECT L.SiteId,L.TransferId,L.SourceCompanyName,L.TargetCompanyName,L.RequestHash,L.RequestJson,L.Status,L.GoodsIssueDocEntry,L.GoodsIssueDocNum,L.GoodsReceiptDocEntry,L.GoodsReceiptDocNum,L.RetryCount,L.ErrorMessage,L.CreateDate,L.UpdateDate
FROM dbo.INT_IntercompanyTransfer L INNER JOIN Requested R ON R.TransferId=L.TransferId WHERE L.SiteId='PRD-TIF' ORDER BY L.CreateDate,L.TransferId;
WITH Requested(TransferId,ShortReference) AS (SELECT TransferId,ShortReference FROM (VALUES /*SCOPE*/) V(TransferId,ShortReference))
SELECT R.TransferId,'GI' AS DocumentSide,'TIF_GOLIVE' AS CompanyDatabase,D.DocEntry,D.DocNum,D.DocDate,D.CANCELED COLLATE DATABASE_DEFAULT AS Canceled
FROM Requested R INNER JOIN TIF_GOLIVE.dbo.OIGE D ON D.U_ODoo_Doc=R.TransferId OR LEFT(ISNULL(D.Comments,''),LEN('ODOO_TRANSFER:'+R.TransferId+'|'))='ODOO_TRANSFER:'+R.TransferId+'|' OR D.Ref2=R.ShortReference
UNION ALL
SELECT R.TransferId,'GR','SBO_STL_GOLIVE',D.DocEntry,D.DocNum,D.DocDate,D.CANCELED COLLATE DATABASE_DEFAULT
FROM Requested R INNER JOIN SBO_STL_GOLIVE.dbo.OIGN D ON D.U_ODoo_Doc=R.TransferId OR LEFT(ISNULL(D.Comments,''),LEN('ODOO_TRANSFER:'+R.TransferId+'|'))='ODOO_TRANSFER:'+R.TransferId+'|' OR D.Ref2=R.ShortReference;
'@
        $command.CommandText = $command.CommandText.Replace('/*SCOPE*/',($values -join ','))
        $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($command)
        $data = [System.Data.DataSet]::new()
        $connection.Open()
        $null = $adapter.Fill($data)
        $sets = @(foreach ($table in $data.Tables) {
            $rows = @(foreach ($row in $table.Rows) {
                $item = [ordered]@{}
                foreach ($column in $table.Columns) {
                    $v = $row[$column.ColumnName]
                    if ($v -is [DBNull]) { $v=$null }
                    elseif ($v -is [datetime]) { $v=$v.ToString('yyyy-MM-dd HH:mm:ss.fff',[Globalization.CultureInfo]::InvariantCulture) }
                    $item[$column.ColumnName]=$v
                }
                [pscustomobject]$item
            })
            [pscustomobject]@{Rows=$rows}
        })
        return [pscustomobject]@{Clock=$sets[0].Rows[0];Ledger=@($sets[1].Rows);Documents=@($sets[2].Rows)}
    } finally { $connection.Dispose() }
}

function Assert-OriginalRequest($Record) {
    $original = $originalRequests[$Record.TransferId]
    if ($null -eq $original -or $Record.SiteId -ne 'PRD-TIF' -or
        $Record.SourceCompanyName -ne 'TIF_GOLIVE' -or $Record.TargetCompanyName -ne 'SBO_STL_GOLIVE' -or
        -not [string]::Equals($Record.RequestHash,$original.Hash,[StringComparison]::Ordinal) -or
        -not [string]::Equals($Record.RequestJson,$original.Json,[StringComparison]::Ordinal) -or
        -not [string]::Equals((Get-Hash $Record.RequestJson),$Record.RequestHash,[StringComparison]::Ordinal)) {
        throw "Original payload/hash mismatch: $($Record.TransferId)"
    }
    $body = $Record.RequestJson | ConvertFrom-Json
    if ($body.transferId -ne $Record.TransferId -or $body.siteId -ne 'PRD-TIF' -or
        $body.sourceCompanyName -ne 'TIF_GOLIVE' -or $body.targetCompanyName -ne 'SBO_STL_GOLIVE') {
        throw "Payload identity mismatch: $($Record.TransferId)"
    }
}

function Assert-DocumentSet($Record,$Documents,[switch]$RequireCompleted) {
    $docs = @($Documents | Where-Object TransferId -EQ $Record.TransferId)
    $gi = @($docs | Where-Object DocumentSide -EQ 'GI')
    $gr = @($docs | Where-Object DocumentSide -EQ 'GR')
    if ($gi.Count -gt 1 -or $gr.Count -gt 1 -or @($docs | Where-Object Canceled -NE 'N').Count -gt 0) {
        throw "Duplicate/canceled SAP document requires inspection: $($Record.TransferId)"
    }
    if ($RequireCompleted -or $Record.Status -eq 'COMPLETED') {
        if ($Record.Status -ne 'COMPLETED' -or $gi.Count -ne 1 -or $gr.Count -ne 1 -or
            $gi[0].DocEntry -ne $Record.GoodsIssueDocEntry -or $gi[0].DocNum -ne $Record.GoodsIssueDocNum -or
            $gr[0].DocEntry -ne $Record.GoodsReceiptDocEntry -or $gr[0].DocNum -ne $Record.GoodsReceiptDocNum) {
            throw "Completed ledger/SAP references do not match: $($Record.TransferId)"
        }
    }
}

$before = Get-Snapshot @($originalScope.TransferId)
if ($before.Ledger.Count -ne 30) { throw 'Original 30 ledger rows are not all present.' }
foreach ($row in $before.Ledger) {
    Assert-OriginalRequest $row
    Assert-DocumentSet $row $before.Documents
}
$excluded = @($before.Ledger | Where-Object TransferId -EQ $excludedId)[0]
Assert-DocumentSet $excluded $before.Documents -RequireCompleted
$candidates = @($before.Ledger | Where-Object { $_.TransferId -in $allowedIds -and $_.TransferId -notin $previouslyAttemptedIds })
$plan = [ordered]@{
    Execute=[bool]$Execute;Endpoint=$baseUrl;ServerTime=$before.Clock.ServerLocalTime
    ExcludedCompletedTransferId=$excludedId;AllowedTransferIds=$allowedIds
    ExcludedPreviouslyAttemptedIds=$previouslyAttemptedIds;PreviousJournal=$ResumeAfterJournal
    AlreadyCompleted=@($candidates | Where-Object Status -EQ 'COMPLETED' | Select-Object -ExpandProperty TransferId)
    Pending=@($candidates | Where-Object Status -NE 'COMPLETED' | Select-Object TransferId,Status,RequestHash)
    Policy='Sequential; original payload string; one POST per TransferId across these journals; no automatic retry; stop on unknown errors/timeouts. A verified stock-only failure with no SAP document may be recorded and skipped.'
}
if (-not $Execute) {
    $plan | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'Remaining29_Preview.json') -Encoding utf8
    [pscustomobject]@{Mode='Preview';ServerTime=$before.Clock.ServerLocalTime;PendingCount=$plan.Pending.Count;AlreadyCompletedCount=$plan.AlreadyCompleted.Count;PayloadHashesVerified=$candidates.Count;DocumentCount=$before.Documents.Count;Endpoint=$baseUrl} | ConvertTo-Json
    exit
}

$runFolder = Join-Path $PSScriptRoot ('send29-'+[datetime]::UtcNow.ToString('yyyyMMdd-HHmmss',[Globalization.CultureInfo]::InvariantCulture)+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
$null = New-Item -ItemType Directory -Path $runFolder
$journalPath = Join-Path $runFolder 'Journal.json'
$journal = [ordered]@{Authorization='User explicitly requested sending the remaining 29 with original TransferIds/payloads';Plan=$plan;Attempts=[Collections.Generic.List[object]]::new();Stopped=$false;Error=$null}
function Save-Journal { $journal | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $journalPath -Encoding utf8 }
Save-Journal
Write-Output "RUN_FOLDER=$runFolder"

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect=$false
$handler.UseProxy=$false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
$client.DefaultRequestVersion=[Version]'1.1'
$client.DefaultVersionPolicy=[Net.Http.HttpVersionPolicy]::RequestVersionExact
try {
    foreach ($planned in $candidates) {
        $id=$planned.TransferId
        $current=Get-Snapshot @($id)
        if ($current.Ledger.Count -ne 1) { throw "Ledger row missing before POST: $id" }
        $row=$current.Ledger[0]
        Assert-OriginalRequest $row
        Assert-DocumentSet $row $current.Documents
        if ($row.Status -eq 'COMPLETED') { Write-Output "SKIP already completed $id"; continue }
        if (@($journal.Attempts | Where-Object TransferId -EQ $id).Count -gt 0) { throw "TransferId already attempted in this run: $id" }
        $attempt=[ordered]@{TransferId=$id;RequestHash=$row.RequestHash;StartedUtc=[datetime]::UtcNow.ToString('o');Outcome='Sending';HttpStatus=$null;Message=$null;ApiData=$null;VerifiedLedger=$null;VerifiedDocuments=$null}
        $journal.Attempts.Add($attempt)
        Save-Journal
        Write-Output "POST_ONCE $id"
        $cts=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(120))
        $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post,$baseUrl)
        $request.Content=[Net.Http.StringContent]::new([string]$row.RequestJson,[Text.Encoding]::UTF8,'application/json')
        $request.Headers.ConnectionClose=$true
        $response=$null
        try {
            $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cts.Token).GetAwaiter().GetResult()
            $attempt.HttpStatus=[int]$response.StatusCode
            $responseText=$response.Content.ReadAsStringAsync($cts.Token).GetAwaiter().GetResult()
            $responseText | Set-Content -LiteralPath (Join-Path $runFolder ($id+'.response.json')) -Encoding utf8
            $body=$responseText | ConvertFrom-Json
            $attempt.Message=$body.message
            $attempt.ApiData=$body.data
            if ($attempt.HttpStatus -ne 200 -or $body.success -ne $true -or $body.data.status -ne 'COMPLETED' -or $body.data.transferId -ne $id -or $body.data.siteId -ne 'PRD-TIF') {
                $attempt.Outcome='ApiFailure'
                if ($ContinueOnStockError -and $attempt.HttpStatus -eq 500 -and
                    ([string]$body.message).StartsWith('Insufficient batch stock for automatic selection.') -and
                    $body.data.transferId -eq $id -and $body.data.siteId -eq 'PRD-TIF') {
                    $failed=Get-Snapshot @($id)
                    if ($failed.Ledger.Count -eq 1 -and $failed.Documents.Count -eq 0 -and
                        $failed.Ledger[0].Status -eq 'ERROR' -and $failed.Ledger[0].ErrorMessage -eq $body.message) {
                        $attempt.Outcome='BusinessErrorVerified'
                        $attempt.VerifiedLedger=$failed.Ledger[0] | Select-Object SiteId,TransferId,Status,GoodsIssueDocEntry,GoodsIssueDocNum,GoodsReceiptDocEntry,GoodsReceiptDocNum,RetryCount,ErrorMessage,CreateDate,UpdateDate
                        $attempt.VerifiedDocuments=@()
                        Save-Journal
                        Write-Output "STOCK_ERROR_NOT_RETRIED $id $($body.message)"
                        continue
                    }
                }
                throw "API did not complete $id; HTTP $($attempt.HttpStatus): $($body.message)"
            }
            $after=Get-Snapshot @($id)
            if ($after.Ledger.Count -ne 1) { throw "Ledger row missing after POST: $id" }
            Assert-OriginalRequest $after.Ledger[0]
            Assert-DocumentSet $after.Ledger[0] $after.Documents -RequireCompleted
            if ([string]$body.data.goodsIssue.documentEntry -ne [string]$after.Ledger[0].GoodsIssueDocEntry -or
                [string]$body.data.goodsReceipt.documentEntry -ne [string]$after.Ledger[0].GoodsReceiptDocEntry) {
                throw "API document references differ from readback: $id"
            }
            $attempt.VerifiedLedger=$after.Ledger[0] | Select-Object SiteId,TransferId,Status,GoodsIssueDocEntry,GoodsIssueDocNum,GoodsReceiptDocEntry,GoodsReceiptDocNum,RetryCount,ErrorMessage,CreateDate,UpdateDate
            $attempt.VerifiedDocuments=$after.Documents
            $attempt.Outcome='CompletedVerified'
            Save-Journal
            Write-Output "COMPLETED_VERIFIED $id GI=$($after.Ledger[0].GoodsIssueDocNum) GR=$($after.Ledger[0].GoodsReceiptDocNum)"
        } catch {
            if ($attempt.Outcome -eq 'Sending') { $attempt.Outcome='Uncertain' }
            $attempt.Message=$_.Exception.Message.Replace([string]$db.Password,'<REDACTED>')
            Save-Journal
            throw
        } finally {
            if ($null -ne $response) { $response.Dispose() }
            $request.Dispose()
            $cts.Dispose()
        }
    }
} catch {
    $journal.Stopped=$true
    $journal.Error=$_.Exception.Message.Replace([string]$db.Password,'<REDACTED>')
    Save-Journal
    Write-Output ('STOPPED '+$journal.Error)
} finally { $client.Dispose() }

try {
    $final=Get-Snapshot @($originalScope.TransferId)
    $final | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runFolder 'FinalSnapshot.json') -Encoding utf8
    $final.Ledger | Select-Object SiteId,TransferId,Status,GoodsIssueDocEntry,GoodsIssueDocNum,GoodsReceiptDocEntry,GoodsReceiptDocNum,RetryCount,ErrorMessage,CreateDate,UpdateDate | Export-Csv -LiteralPath (Join-Path $runFolder 'FinalLedger.csv') -NoTypeInformation -Encoding utf8
    [pscustomobject]@{RunFolder=$runFolder;Stopped=$journal.Stopped;Attempted=$journal.Attempts.Count;CompletedVerified=@($journal.Attempts | Where-Object Outcome -EQ 'CompletedVerified').Count;FinalServerTime=$final.Clock.ServerLocalTime;Original30Statuses=@($final.Ledger | Group-Object Status | Select-Object Name,Count);SapDocumentCount=$final.Documents.Count;Error=$journal.Error} | ConvertTo-Json -Depth 6
} catch { Write-Output ('FINAL_READBACK_FAILED '+$_.Exception.Message.Replace([string]$db.Password,'<REDACTED>')); exit 2 }
if ($journal.Stopped) { exit 1 }

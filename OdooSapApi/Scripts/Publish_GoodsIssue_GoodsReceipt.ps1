$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectDirectory "OdooSapApi.sln"
$projectPath = Join-Path $projectDirectory "OdooSapApi.csproj"
$publishPath = "D:\API_Publish\GoodsIssue_GoodsReceipt"

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

dotnet test $solutionPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed. Goods Issue / Goods Receipt API was not published."
}

dotnet publish $projectPath --configuration Release --output $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Goods Issue / Goods Receipt publish failed."
}

$requiredFiles = @(
    "OdooSapApi.exe",
    "OdooSapApi.dll",
    "appsettings.json"
)

foreach ($requiredFile in $requiredFiles) {
    $requiredPath = Join-Path $publishPath $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Published file is missing: $requiredPath"
    }
}

Write-Host "Goods Issue / Goods Receipt API published successfully: $publishPath"
Get-ChildItem -LiteralPath $publishPath

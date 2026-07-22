$ErrorActionPreference = "Stop"

$projectDirectory = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $projectDirectory "OdooSapApi.sln"
$projectPath = Join-Path $projectDirectory "OdooSapApi.csproj"
$publishPath = "D:\API_Publish\Delivery"

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

dotnet test $solutionPath --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed. Delivery was not published."
}

dotnet publish $projectPath --configuration Release --output $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Delivery publish failed."
}

Write-Host "Delivery API published successfully: $publishPath"
Get-ChildItem -LiteralPath $publishPath

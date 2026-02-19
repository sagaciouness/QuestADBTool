param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$VersionTag = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "QuestADBTool.csproj"
$publishRoot = Join-Path $projectRoot "artifacts\publish"
$stageRoot = Join-Path $projectRoot "artifacts\stage"
$adbSource = Join-Path $projectRoot "adb"

if ([string]::IsNullOrWhiteSpace($VersionTag)) {
    $VersionTag = Get-Date -Format "yyyyMMdd_HHmmss"
}

$publishDir = Join-Path $publishRoot "$Configuration-$Runtime"
$stageDir = Join-Path $stageRoot "QuestADBTool_$VersionTag"
$zipPath = Join-Path $projectRoot "artifacts\QuestADBTool_$VersionTag.zip"

Write-Host "==> Clean old outputs"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "==> dotnet publish"
dotnet publish $projectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $publishDir

Write-Host "==> Stage package"
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
Copy-Item (Join-Path $publishDir "*") $stageDir -Recurse -Force

if (Test-Path $adbSource) {
    Copy-Item $adbSource (Join-Path $stageDir "adb") -Recurse -Force
}

Write-Host "==> Create zip"
New-Item -ItemType Directory -Path (Split-Path -Parent $zipPath) -Force | Out-Null
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal -Force

Write-Host ""
Write-Host "Done: $zipPath"

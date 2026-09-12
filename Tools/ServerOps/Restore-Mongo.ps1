param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,

    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [string]$ConfirmDatabaseName,

    [switch]$DropExisting
)

$ErrorActionPreference = "Stop"
if ($ConfirmDatabaseName -cne $Database) {
    throw "ConfirmDatabaseName must exactly match Database."
}

$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI before running this script. Do not place credentials in source control."
}

$resolvedBackup = [System.IO.Path]::GetFullPath($BackupPath)
$manifestPath = Join-Path $resolvedBackup "backup-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Backup manifest not found: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$sourceDatabase = [string]$manifest.Database
if ($sourceDatabase -notmatch '^[A-Za-z0-9_-]+$') {
    throw "Backup manifest does not contain a source database name."
}

$databaseDump = Join-Path $resolvedBackup $sourceDatabase
if (-not (Test-Path -LiteralPath $databaseDump -PathType Container)) {
    throw "Backup database directory not found: $databaseDump"
}

$arguments = @(
    "--stopOnError"
    "--nsInclude=$sourceDatabase.*"
)
if ($sourceDatabase -cne $Database) {
    $arguments += "--nsFrom=$sourceDatabase.*"
    $arguments += "--nsTo=$Database.*"
}
if ($DropExisting) {
    $arguments += "--drop"
}
$arguments += $resolvedBackup

. "$PSScriptRoot/MongoTool.ps1"
Invoke-MongoTool -Tool mongorestore -Arguments $arguments
if ($LASTEXITCODE -ne 0) {
    throw "mongorestore failed with exit code $LASTEXITCODE"
}

Write-Host "MongoDB restore completed for database: $Database"

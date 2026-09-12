param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot "backups")
)

$ErrorActionPreference = "Stop"
$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI before running this script. Do not place credentials in source control."
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")
$backupPath = Join-Path $resolvedOutput "$Database-$timestamp"

. "$PSScriptRoot/MongoTool.ps1"
Invoke-MongoTool -Tool mongodump -Arguments @("--db=$Database", "--out=$backupPath")
if ($LASTEXITCODE -ne 0) {
    throw "mongodump failed with exit code $LASTEXITCODE"
}

$manifest = @{
    Database = $Database
    CreatedAtUtc = [DateTime]::UtcNow.ToString("O")
    Path = $backupPath
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupPath "backup-manifest.json") -Encoding UTF8
Write-Host "MongoDB backup completed: $backupPath"
Write-Output $backupPath

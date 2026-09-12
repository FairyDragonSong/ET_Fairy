param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,

    [Parameter(Mandatory = $true)][string]$OutputDirectory,

    [Parameter(Mandatory = $true)][string]$SigningPrivateKeyFile,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$SigningKeyId
)

$ErrorActionPreference = "Stop"
$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI before running this script. Do not place credentials in source control."
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
. "$PSScriptRoot/BackupRestore.Common.ps1"
$resolvedOutput=Assert-BackupPathOutsideRepository $resolvedOutput 'OutputDirectory'
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")
$backupPath = Join-Path $resolvedOutput "$Database-$timestamp"

. "$PSScriptRoot/MongoTool.ps1"
Invoke-MongoTool -Tool mongodump -Arguments @("--db=$Database", "--out=$backupPath")
if ($LASTEXITCODE -ne 0) {
    throw "mongodump failed with exit code $LASTEXITCODE"
}

$backupId="$Database-$timestamp"
& "$PSScriptRoot/New-SignedMongoBackupManifest.ps1" -BackupPath $backupPath -SourceDatabase $Database `
    -BackupId $backupId -SigningPrivateKeyFile $SigningPrivateKeyFile -SigningKeyId $SigningKeyId|Out-Null
Write-Host "MongoDB backup completed: $backupPath"
Write-Output $backupPath

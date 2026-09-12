param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,

    [Parameter(Mandatory = $true)]
    [string]$BackupPath,

    [Parameter(Mandatory = $true)]
    [string]$ConfirmDatabaseName,

    [switch]$DropExisting,

    [Parameter(Mandatory = $true)][string]$AuthorizationFile,
    [Parameter(Mandatory = $true)][string]$OperationId,
    [Parameter(Mandatory = $true)][string]$BackupTrustedPublicKeyFile,
    [Parameter(Mandatory = $true)][string]$BackupExpectedSigningKeyId,
    [Parameter(Mandatory = $true)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory = $true)][string]$ExpectedRequesterKeyId,
    [Parameter(Mandatory = $true)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory = $true)][string]$ExpectedApproverKeyId,
    [string]$OperationLedgerDirectory,
    [switch]$VerifyOnly,
    [switch]$TestOnlyConsumeAuthorization
)

$ErrorActionPreference = "Stop"
if ($ConfirmDatabaseName -cne $Database) {
    throw "ConfirmDatabaseName must exactly match Database."
}

. "$PSScriptRoot/BackupRestore.Common.ps1"
$resolvedBackup = Assert-BackupPathOutsideRepository $BackupPath 'BackupPath'
$authorizationRaw=& "$PSScriptRoot/Test-MongoRestoreAuthorization.ps1" -AuthorizationFile $AuthorizationFile `
    -ExpectedOperationId $OperationId -BackupPath $resolvedBackup -BackupTrustedPublicKeyFile $BackupTrustedPublicKeyFile `
    -BackupExpectedSigningKeyId $BackupExpectedSigningKeyId -RequesterPublicKeyFile $RequesterPublicKeyFile `
    -ExpectedRequesterKeyId $ExpectedRequesterKeyId -ApproverPublicKeyFile $ApproverPublicKeyFile `
    -ExpectedApproverKeyId $ExpectedApproverKeyId -ExpectedTargetDatabase $Database -ExpectedDropExisting ([bool]$DropExisting)
$authorization=($authorizationRaw -join [Environment]::NewLine)|ConvertFrom-Json
$sourceDatabase=[string]$authorization.SourceDatabase

$databaseDump = Join-Path $resolvedBackup $sourceDatabase
if (-not (Test-Path -LiteralPath $databaseDump -PathType Container)) {
    throw "Backup database directory not found: $databaseDump"
}
if($TestOnlyConsumeAuthorization -and -not $VerifyOnly){throw 'TestOnlyConsumeAuthorization requires VerifyOnly'}
if($OperationLedgerDirectory){
    $ledgerRoot=[IO.Path]::GetFullPath($OperationLedgerDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if($ledgerRoot.StartsWith($resolvedBackup+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Operation ledger cannot be inside the signed backup artifact'
    }
}
if($VerifyOnly){
    if($TestOnlyConsumeAuthorization){
        if(-not $OperationLedgerDirectory){throw 'OperationLedgerDirectory is required when consuming an authorization'}
        $testLedger=Start-RestoreAuthorizationConsumption $OperationLedgerDirectory $OperationId $AuthorizationFile
        Complete-RestoreAuthorizationConsumption $testLedger $true
    }
    [ordered]@{Passed=$true;VerifyOnly=$true;OperationId=$OperationId;BackupId=$authorization.BackupId;
        SourceDatabase=$sourceDatabase;TargetDatabase=$Database;DropExisting=[bool]$DropExisting}|ConvertTo-Json
    return
}

$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI before running this script. Do not place credentials in source control."
}
if(-not $OperationLedgerDirectory){throw 'OperationLedgerDirectory is required for an actual restore'}
$operationLedger=Start-RestoreAuthorizationConsumption $OperationLedgerDirectory $OperationId $AuthorizationFile

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
$restoreSucceeded=$false
try{
    Invoke-MongoTool -Tool mongorestore -Arguments $arguments
    if ($LASTEXITCODE -ne 0) {throw "mongorestore failed with exit code $LASTEXITCODE"}
    $restoreSucceeded=$true
}finally{Complete-RestoreAuthorizationConsumption $operationLedger $restoreSucceeded}

Write-Host "MongoDB restore completed for database: $Database"

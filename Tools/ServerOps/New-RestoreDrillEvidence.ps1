param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$DrillId,
    [Parameter(Mandatory)][string]$PolicyFile,[Parameter(Mandatory)][string]$PolicyRequesterPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedPolicyRequesterKeyId,[Parameter(Mandatory)][string]$PolicyApproverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedPolicyApproverKeyId,[Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedBackupSigningKeyId,[Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedDrillObserverKeyId,[Parameter(Mandatory)][string]$DrillObserverPrivateKeyFile,
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$RestoreEvidenceFile,
    [Parameter(Mandatory)][string]$RestoreOperationLedgerFile,[Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1"
$policyParameters=@{PolicyFile=$PolicyFile;RequesterPublicKeyFile=$PolicyRequesterPublicKeyFile;
    ExpectedRequesterKeyId=$ExpectedPolicyRequesterKeyId;ApproverPublicKeyFile=$PolicyApproverPublicKeyFile;
    ExpectedApproverKeyId=$ExpectedPolicyApproverKeyId;BackupSigningPublicKeyFile=$BackupSigningPublicKeyFile;
    ExpectedBackupSigningKeyId=$ExpectedBackupSigningKeyId;DrillObserverPublicKeyFile=$DrillObserverPublicKeyFile;
    ExpectedDrillObserverKeyId=$ExpectedDrillObserverKeyId}
$policyRaw=& "$PSScriptRoot/Test-DisasterRecoveryPolicy.ps1" @policyParameters
$policy=($policyRaw -join [Environment]::NewLine)|ConvertFrom-Json
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath `
    -TrustedPublicKeyFile $BackupSigningPublicKeyFile -ExpectedSigningKeyId $ExpectedBackupSigningKeyId `
    -ExpectedSourceDatabase $policy.Database
$backup=($backupRaw -join [Environment]::NewLine)|ConvertFrom-Json
$observerPrivate=Get-BackupKeyFingerprint $DrillObserverPrivateKeyFile 'Drill observer' $true
$observerPublic=Get-BackupKeyFingerprint $DrillObserverPublicKeyFile 'Drill observer' $false
if($observerPrivate-cne$observerPublic){throw 'Drill observer private/public keys do not match'}
$restoreFile=[IO.Path]::GetFullPath($RestoreEvidenceFile);$ledgerFile=[IO.Path]::GetFullPath($RestoreOperationLedgerFile)
$restore=Get-Content -LiteralPath $restoreFile -Raw|ConvertFrom-Json -Depth 15 -DateKind String
$ledger=Get-Content -LiteralPath $ledgerFile -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$countChecks=@($restore.RestoreCheck.counts.PSObject.Properties)
if($countChecks.Count-lt1){throw 'Restore evidence contains no collection verification'}
foreach($check in $countChecks){
    if([long]$check.Value.source-ne[long]$check.Value.restored-or-not[bool]$check.Value.indexesVerified){
        throw 'Restore collection count or index verification failed'
    }
}
$started=[DateTimeOffset]::MinValue;$completed=[DateTimeOffset]::MinValue
if($restore.Result-ne'PASS_DATABASE_CHECKS'-or$restore.SourceDatabase-cne$policy.Database-or
   $restore.BackupManifestSha256-cne$backup.ManifestSha256-or$restore.RestoreOperationId-cne$ledger.OperationId-or
   $ledger.Status-ne'Completed'-or$ledger.AuthorizationSha256-notmatch'^[a-f0-9]{64}$'-or
   -not[DateTimeOffset]::TryParse([string]$restore.StartedAtUtc,[ref]$started)-or
   -not[DateTimeOffset]::TryParse([string]$restore.CompletedAtUtc,[ref]$completed)-or$completed-le$started-or
   ($completed-$started).TotalMinutes-gt[double]$policy.RtoMinutes-or
   $completed-lt[DateTimeOffset]::UtcNow.AddDays(-[int]$policy.MaximumDrillAgeDays)-or
   $completed-gt[DateTimeOffset]::UtcNow.AddMinutes(5)){
    throw 'Restore evidence does not satisfy the signed disaster recovery policy'
}
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory';New-Item -ItemType Directory $output -Force|Out-Null
$file=Join-Path $output "$DrillId.restore-drill.json";if((Test-Path $file)-or(Test-Path "$file.sig")){throw 'Restore drill evidence already exists'}
$drill=[ordered]@{SchemaVersion=1;Product='ET.Server';DrillId=$DrillId;PolicyId=$policy.PolicyId;PolicySha256=$policy.PolicySha256;
    DeploymentId=$policy.DeploymentId;Database=$policy.Database;BackupId=$backup.BackupId;BackupManifestSha256=$backup.ManifestSha256;
    RestoreOperationId=$ledger.OperationId;RestoreAuthorizationSha256=$ledger.AuthorizationSha256;
    RestoreEvidenceSha256=(Get-FileHash $restoreFile -Algorithm SHA256).Hash.ToLowerInvariant();
    StartedAtUtc=$started.ToString('O');CompletedAtUtc=$completed.ToString('O');DurationSeconds=[Math]::Round(($completed-$started).TotalSeconds,3);
    RtoMinutes=$policy.RtoMinutes;ObserverKeyId=$ExpectedDrillObserverKeyId;ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');Result='PASS'}
$bytes=$script:BackupRestoreUtf8.GetBytes(($drill|ConvertTo-Json -Depth 8))
try{[IO.File]::WriteAllBytes($file,$bytes);[IO.File]::WriteAllBytes("$file.sig",(New-BackupSignature $bytes $DrillObserverPrivateKeyFile 'Drill observer'))
    [ordered]@{Passed=$true;DrillFile=$file;DrillId=$DrillId;DurationSeconds=$drill.DurationSeconds;RtoMinutes=$policy.RtoMinutes;
        DrillSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}catch{foreach($path in @($file,"$file.sig")){if(Test-Path $path){Remove-Item $path -Force}};throw}

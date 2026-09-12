param(
    [Parameter(Mandatory)][string]$DrillFile,[Parameter(Mandatory)][string]$PolicyFile,
    [Parameter(Mandatory)][string]$PolicyRequesterPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyRequesterKeyId,
    [Parameter(Mandatory)][string]$PolicyApproverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyApproverKeyId,
    [Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedBackupSigningKeyId,
    [Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedDrillObserverKeyId,
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$RestoreEvidenceFile,
    [Parameter(Mandatory)][string]$RestoreOperationLedgerFile
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1"
$policyRaw=& "$PSScriptRoot/Test-DisasterRecoveryPolicy.ps1" -PolicyFile $PolicyFile `
    -RequesterPublicKeyFile $PolicyRequesterPublicKeyFile -ExpectedRequesterKeyId $ExpectedPolicyRequesterKeyId `
    -ApproverPublicKeyFile $PolicyApproverPublicKeyFile -ExpectedApproverKeyId $ExpectedPolicyApproverKeyId `
    -BackupSigningPublicKeyFile $BackupSigningPublicKeyFile -ExpectedBackupSigningKeyId $ExpectedBackupSigningKeyId `
    -DrillObserverPublicKeyFile $DrillObserverPublicKeyFile -ExpectedDrillObserverKeyId $ExpectedDrillObserverKeyId
$policy=($policyRaw -join [Environment]::NewLine)|ConvertFrom-Json
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath -TrustedPublicKeyFile $BackupSigningPublicKeyFile `
    -ExpectedSigningKeyId $ExpectedBackupSigningKeyId -ExpectedSourceDatabase $policy.Database
$backup=($backupRaw -join [Environment]::NewLine)|ConvertFrom-Json
$file=[IO.Path]::GetFullPath($DrillFile);$bytes=[IO.File]::ReadAllBytes($file)
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.sig")) $DrillObserverPublicKeyFile 'Drill observer'
$drill=$script:BackupRestoreUtf8.GetString($bytes)|ConvertFrom-Json -Depth 10 -DateKind String
$restoreFile=[IO.Path]::GetFullPath($RestoreEvidenceFile);$restore=Get-Content $restoreFile -Raw|ConvertFrom-Json -Depth 15 -DateKind String
$ledger=Get-Content ([IO.Path]::GetFullPath($RestoreOperationLedgerFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$started=[DateTimeOffset]::MinValue;$completed=[DateTimeOffset]::MinValue;$observed=[DateTimeOffset]::MinValue
$restoreStarted=[DateTimeOffset]::MinValue;$restoreCompleted=[DateTimeOffset]::MinValue
$countChecks=@($restore.RestoreCheck.counts.PSObject.Properties)
if($countChecks.Count-lt1){throw 'Restore evidence contains no collection verification'}
foreach($check in $countChecks){
    if([long]$check.Value.source-ne[long]$check.Value.restored-or-not[bool]$check.Value.indexesVerified){throw 'Restore collection verification is invalid'}
}
if($drill.SchemaVersion-ne1-or$drill.Product-ne'ET.Server'-or$drill.Result-ne'PASS'-or
   $drill.PolicyId-cne$policy.PolicyId-or$drill.PolicySha256-cne$policy.PolicySha256-or$drill.DeploymentId-cne$policy.DeploymentId-or
   $drill.Database-cne$policy.Database-or$drill.BackupId-cne$backup.BackupId-or$drill.BackupManifestSha256-cne$backup.ManifestSha256-or
   $drill.RestoreOperationId-cne$ledger.OperationId-or$ledger.Status-ne'Completed'-or
   $drill.RestoreAuthorizationSha256-cne$ledger.AuthorizationSha256-or
   $drill.RestoreEvidenceSha256-cne(Get-FileHash $restoreFile -Algorithm SHA256).Hash.ToLowerInvariant()-or
   $drill.ObserverKeyId-cne$ExpectedDrillObserverKeyId-or-not[DateTimeOffset]::TryParse([string]$drill.StartedAtUtc,[ref]$started)-or
   -not[DateTimeOffset]::TryParse([string]$drill.CompletedAtUtc,[ref]$completed)-or-not[DateTimeOffset]::TryParse([string]$drill.ObservedAtUtc,[ref]$observed)-or
   -not[DateTimeOffset]::TryParse([string]$restore.StartedAtUtc,[ref]$restoreStarted)-or-not[DateTimeOffset]::TryParse([string]$restore.CompletedAtUtc,[ref]$restoreCompleted)-or
   $started-ne$restoreStarted-or$completed-ne$restoreCompleted-or$restore.SourceDatabase-cne$policy.Database-or
   $restore.BackupManifestSha256-cne$backup.ManifestSha256-or[int]$drill.RtoMinutes-ne[int]$policy.RtoMinutes-or
   $completed-le$started-or$observed-lt$completed-or$observed-gt[DateTimeOffset]::UtcNow.AddMinutes(5)-or
   [Math]::Abs([double]$drill.DurationSeconds-($completed-$started).TotalSeconds)-gt0.01-or
   [double]$drill.DurationSeconds-gt([int]$policy.RtoMinutes*60)-or$completed-lt[DateTimeOffset]::UtcNow.AddDays(-[int]$policy.MaximumDrillAgeDays)-or
   $restore.Result-ne'PASS_DATABASE_CHECKS'-or$restore.RestoreOperationId-cne$ledger.OperationId){throw 'Signed restore drill evidence is invalid or no longer satisfies policy'}
[ordered]@{Passed=$true;DrillId=$drill.DrillId;PolicyId=$policy.PolicyId;DeploymentId=$policy.DeploymentId;
    Database=$policy.Database;DurationSeconds=[double]$drill.DurationSeconds;RtoMinutes=[int]$policy.RtoMinutes;
    CompletedAtUtc=$drill.CompletedAtUtc;DrillSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json

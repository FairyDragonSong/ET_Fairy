param(
    [Parameter(Mandatory)][string]$PolicyFile,[Parameter(Mandatory)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedRequesterKeyId,[Parameter(Mandatory)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedApproverKeyId,[Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedBackupSigningKeyId,[Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedDrillObserverKeyId
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1"
$file=[IO.Path]::GetFullPath($PolicyFile);$bytes=[IO.File]::ReadAllBytes($file)
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.requester.sig")) $RequesterPublicKeyFile 'Policy requester'
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.approver.sig")) $ApproverPublicKeyFile 'Policy approver'
$fingerprints=@(
    Get-BackupKeyFingerprint $RequesterPublicKeyFile 'Policy requester' $false
    Get-BackupKeyFingerprint $ApproverPublicKeyFile 'Policy approver' $false
    Get-BackupKeyFingerprint $BackupSigningPublicKeyFile 'Backup signing' $false
    Get-BackupKeyFingerprint $DrillObserverPublicKeyFile 'Drill observer' $false)
if(@($fingerprints|Select-Object -Unique).Count-ne4){throw 'Disaster recovery trust roles are not separated'}
try{$policy=$script:BackupRestoreUtf8.GetString($bytes)|ConvertFrom-Json -Depth 8 -DateKind String}catch{throw 'Disaster recovery policy JSON is invalid'}
$effective=[DateTimeOffset]::MinValue;$review=[DateTimeOffset]::MinValue
if($policy.SchemaVersion-ne1-or$policy.Product-ne'ET.Server'-or$policy.PolicyId-notmatch'^[0-9A-Za-z._-]{3,80}$'-or
   $policy.DeploymentId-notmatch'^[0-9A-Za-z._-]{3,80}$'-or$policy.Database-notmatch'^[A-Za-z0-9_-]+$'-or
   $policy.RequesterKeyId-cne$ExpectedRequesterKeyId-or$policy.ApproverKeyId-cne$ExpectedApproverKeyId-or
   $policy.BackupSigningKeyId-cne$ExpectedBackupSigningKeyId-or$policy.DrillObserverKeyId-cne$ExpectedDrillObserverKeyId-or
   $policy.BackupSigningPublicKeySha256-cne$fingerprints[2]-or$policy.DrillObserverPublicKeySha256-cne$fingerprints[3]-or
   $policy.RequestedBy-ceq$policy.ApprovedBy-or-not[DateTimeOffset]::TryParse([string]$policy.EffectiveAtUtc,[ref]$effective)-or
   -not[DateTimeOffset]::TryParse([string]$policy.ReviewDueUtc,[ref]$review)-or$effective-gt[DateTimeOffset]::UtcNow.AddMinutes(5)-or
   $review-le[DateTimeOffset]::UtcNow-or$review-gt$effective.AddDays(365)-or[int]$policy.RpoMinutes-lt1-or[int]$policy.RpoMinutes-gt1440-or
   [int]$policy.RtoMinutes-lt1-or[int]$policy.RtoMinutes-gt1440-or[int]$policy.BackupIntervalMinutes-lt1-or
   [int]$policy.BackupIntervalMinutes-gt[int]$policy.RpoMinutes-or[int]$policy.MaximumDrillAgeDays-lt1-or[int]$policy.MaximumDrillAgeDays-gt180){
    throw 'Disaster recovery policy identity, review window, or objectives are invalid'
}
[ordered]@{Passed=$true;PolicyId=$policy.PolicyId;DeploymentId=$policy.DeploymentId;Database=$policy.Database;
    PolicySha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant();RpoMinutes=[int]$policy.RpoMinutes;
    RtoMinutes=[int]$policy.RtoMinutes;MaximumDrillAgeDays=[int]$policy.MaximumDrillAgeDays;ReviewDueUtc=$policy.ReviewDueUtc}|ConvertTo-Json

param(
    [Parameter(Mandatory)][string]$RecordFile,[Parameter(Mandatory)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedRequesterKeyId,[Parameter(Mandatory)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedApproverKeyId,[Parameter(Mandatory)][string]$ReleaseSigningPublicKeyFile,
    [Parameter(Mandatory)][string]$DeploymentStatePublicKeyFile,[switch]$AllowTestRecord,[switch]$RequireActiveWindow
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1";. "$PSScriptRoot/ServerChange.Common.ps1"
$file=[IO.Path]::GetFullPath($RecordFile);$bytes=[IO.File]::ReadAllBytes($file)
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.requester.sig")) $RequesterPublicKeyFile 'Change requester'
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.approver.sig")) $ApproverPublicKeyFile 'Change approver'
$requester=Get-BackupKeyFingerprint $RequesterPublicKeyFile 'Change requester' $false
$approver=Get-BackupKeyFingerprint $ApproverPublicKeyFile 'Change approver' $false
$release=Get-BackupKeyFingerprint $ReleaseSigningPublicKeyFile 'Release signer' $false
$state=Get-BackupKeyFingerprint $DeploymentStatePublicKeyFile 'Deployment state signer' $false
if(@(@($requester,$approver,$release,$state)|Select-Object -Unique).Count-ne4){throw 'Change approval and deployment trust roles are not separated'}
$record=$script:ServerChangeUtf8.GetString($bytes)|ConvertFrom-Json -Depth 12 -DateKind String
if($record.RequesterKeyId-cne$ExpectedRequesterKeyId-or$record.ApproverKeyId-cne$ExpectedApproverKeyId-or
   $record.RequesterPublicKeySha256-cne$requester-or$record.ApproverPublicKeySha256-cne$approver-or
   $record.ReleaseSigningPublicKeySha256-cne$release-or$record.DeploymentStatePublicKeySha256-cne$state){throw 'Server change signer identity is invalid'}
Assert-ServerChangeRecord $record ([DateTimeOffset]::UtcNow) ([bool]$AllowTestRecord) ([bool]$RequireActiveWindow)
[ordered]@{Passed=$true;RecordId=$record.RecordId;ChangeId=$record.ChangeId;DeploymentId=$record.DeploymentId;
    Environment=$record.Environment;RiskLevel=$record.RiskLevel;Decision=$record.Decision;ChecklistItems=@($record.Checklist).Count;
    RecordSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json

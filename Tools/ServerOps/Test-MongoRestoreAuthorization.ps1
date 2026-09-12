param(
    [Parameter(Mandatory)][string]$AuthorizationFile,[Parameter(Mandatory)][string]$ExpectedOperationId,
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$BackupTrustedPublicKeyFile,
    [Parameter(Mandatory)][string]$BackupExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$RequesterPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedRequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedApproverKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ExpectedTargetDatabase,
    [Parameter(Mandatory)][bool]$ExpectedDropExisting
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"
$file=[IO.Path]::GetFullPath($AuthorizationFile);$bytes=[IO.File]::ReadAllBytes($file)
foreach($signature in @("$file.requester.sig","$file.approver.sig")){if(-not(Test-Path $signature -PathType Leaf)){throw 'Restore approval signature is missing'}}
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.requester.sig")) $RequesterPublicKeyFile 'Restore requester'
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.approver.sig")) $ApproverPublicKeyFile 'Restore approver'
$requesterFingerprint=Get-BackupKeyFingerprint $RequesterPublicKeyFile 'Restore requester' $false
$approverFingerprint=Get-BackupKeyFingerprint $ApproverPublicKeyFile 'Restore approver' $false
$backupFingerprint=Get-BackupKeyFingerprint $BackupTrustedPublicKeyFile 'Backup' $false
if($requesterFingerprint -in @($approverFingerprint,$backupFingerprint)-or$approverFingerprint-eq$backupFingerprint){throw 'Restore trust roles are not separated'}
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath `
    -TrustedPublicKeyFile $BackupTrustedPublicKeyFile -ExpectedSigningKeyId $BackupExpectedSigningKeyId
$backup=($backupRaw -join [Environment]::NewLine)|ConvertFrom-Json
try{$authorization=$script:BackupRestoreUtf8.GetString($bytes)|ConvertFrom-Json -Depth 10 -DateKind String}catch{throw 'Restore authorization JSON is invalid'}
$created=[DateTimeOffset]::MinValue;$expires=[DateTimeOffset]::MinValue
if($authorization.SchemaVersion-ne1-or$authorization.Product-ne'ET.Server'-or$authorization.Action-ne'MongoRestore'-or
   $authorization.OperationId-cne$ExpectedOperationId-or$authorization.RequesterKeyId-cne$ExpectedRequesterKeyId-or
   $authorization.ApproverKeyId-cne$ExpectedApproverKeyId-or$ExpectedRequesterKeyId-ceq$ExpectedApproverKeyId-or
   $authorization.OperationId-notmatch'^[0-9A-Za-z._-]{3,80}$'-or
   $authorization.RequestedBy-notmatch'^[0-9A-Za-z._@-]{3,80}$'-or$authorization.ApprovedBy-notmatch'^[0-9A-Za-z._@-]{3,80}$'-or
   -not[DateTimeOffset]::TryParse([string]$authorization.CreatedAtUtc,[ref]$created)-or
   -not[DateTimeOffset]::TryParse([string]$authorization.ExpiresAtUtc,[ref]$expires)-or
   $created-gt[DateTimeOffset]::UtcNow.AddMinutes(5)-or$expires-le[DateTimeOffset]::UtcNow-or$expires-gt$created.AddHours(4)-or
   $authorization.TargetDatabase-cne$ExpectedTargetDatabase-or[bool]$authorization.DropExisting-ne$ExpectedDropExisting-or
   $authorization.Backup.BackupId-cne$backup.BackupId-or$authorization.Backup.SourceDatabase-cne$backup.SourceDatabase-or
   $authorization.Backup.ManifestSha256-cne$backup.ManifestSha256){throw 'Restore authorization identity, scope, validity, or backup binding is invalid'}
if($authorization.RequestedBy -ceq $authorization.ApprovedBy){throw 'Restore requester and approver identities are identical'}
[ordered]@{Passed=$true;OperationId=$authorization.OperationId;BackupId=$backup.BackupId;SourceDatabase=$backup.SourceDatabase;
    TargetDatabase=$authorization.TargetDatabase;DropExisting=[bool]$authorization.DropExisting;ExpiresAtUtc=$authorization.ExpiresAtUtc}|ConvertTo-Json

param(
    [Parameter(Mandatory)][string]$InventoryFile,[Parameter(Mandatory)][string]$PolicyFile,
    [Parameter(Mandatory)][string]$PolicyRequesterPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyRequesterKeyId,
    [Parameter(Mandatory)][string]$PolicyApproverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyApproverKeyId,
    [Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedBackupSigningKeyId,
    [Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedDrillObserverKeyId,
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$StorageObserverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedStorageObserverKeyId,[Parameter(Mandatory)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedRequesterKeyId,[Parameter(Mandatory)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory)][string]$ExpectedApproverKeyId
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1";. "$PSScriptRoot/BackupStorage.Common.ps1"
$file=[IO.Path]::GetFullPath($InventoryFile);$bytes=[IO.File]::ReadAllBytes($file)
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.observer.sig")) $StorageObserverPublicKeyFile 'Storage observer'
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.requester.sig")) $RequesterPublicKeyFile 'Inventory requester'
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes("$file.approver.sig")) $ApproverPublicKeyFile 'Inventory approver'
$observer=Get-BackupKeyFingerprint $StorageObserverPublicKeyFile 'Storage observer' $false
$requester=Get-BackupKeyFingerprint $RequesterPublicKeyFile 'Inventory requester' $false
$approver=Get-BackupKeyFingerprint $ApproverPublicKeyFile 'Inventory approver' $false
$backupKey=Get-BackupKeyFingerprint $BackupSigningPublicKeyFile 'Backup signing' $false
$drillKey=Get-BackupKeyFingerprint $DrillObserverPublicKeyFile 'Drill observer' $false
if(@(@($observer,$requester,$approver,$backupKey,$drillKey)|Select-Object -Unique).Count-ne5){throw 'Storage inventory trust roles are not separated'}
$policyRaw=& "$PSScriptRoot/Test-DisasterRecoveryPolicy.ps1" -PolicyFile $PolicyFile -RequesterPublicKeyFile $PolicyRequesterPublicKeyFile `
    -ExpectedRequesterKeyId $ExpectedPolicyRequesterKeyId -ApproverPublicKeyFile $PolicyApproverPublicKeyFile -ExpectedApproverKeyId $ExpectedPolicyApproverKeyId `
    -BackupSigningPublicKeyFile $BackupSigningPublicKeyFile -ExpectedBackupSigningKeyId $ExpectedBackupSigningKeyId `
    -DrillObserverPublicKeyFile $DrillObserverPublicKeyFile -ExpectedDrillObserverKeyId $ExpectedDrillObserverKeyId
$policy=($policyRaw-join[Environment]::NewLine)|ConvertFrom-Json
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath -TrustedPublicKeyFile $BackupSigningPublicKeyFile `
    -ExpectedSigningKeyId $ExpectedBackupSigningKeyId -ExpectedSourceDatabase $policy.Database
$backup=($backupRaw-join[Environment]::NewLine)|ConvertFrom-Json
$inventory=$script:BackupRestoreUtf8.GetString($bytes)|ConvertFrom-Json -Depth 12 -DateKind String
if($inventory.StorageObserverKeyId-cne$ExpectedStorageObserverKeyId-or$inventory.StorageObserverPublicKeySha256-cne$observer-or
   $inventory.RequesterKeyId-cne$ExpectedRequesterKeyId-or$inventory.ApproverKeyId-cne$ExpectedApproverKeyId-or
   $inventory.RequestedBy-ceq$inventory.ApprovedBy){throw 'Storage inventory signer identity is invalid'}
Assert-BackupStorageInventory $inventory $policy $backup ([DateTimeOffset]::UtcNow)
[ordered]@{Passed=$true;InventoryId=$inventory.InventoryId;PolicyId=$policy.PolicyId;BackupId=$backup.BackupId;
    Copies=@($inventory.Copies).Count;FailureDomains=@($inventory.Copies.FailureDomain|Select-Object -Unique).Count;
    DailyRestorePoints=[int]$inventory.Retention.DailyRestorePoints;WeeklyRestorePoints=[int]$inventory.Retention.WeeklyRestorePoints;
    MonthlyRestorePoints=[int]$inventory.Retention.MonthlyRestorePoints;InventorySha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json

param(
    [Parameter(Mandatory)][string]$InventoryId,[Parameter(Mandatory)][string]$PolicyFile,
    [Parameter(Mandatory)][string]$PolicyRequesterPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyRequesterKeyId,
    [Parameter(Mandatory)][string]$PolicyApproverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedPolicyApproverKeyId,
    [Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedBackupSigningKeyId,
    [Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedDrillObserverKeyId,
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$CopiesFile,[Parameter(Mandatory)][string]$RetentionFile,
    [Parameter(Mandatory)][ValidateRange(2,5)][int]$MinimumCopies,[Parameter(Mandatory)][ValidateRange(2,5)][int]$MinimumFailureDomains,
    [Parameter(Mandatory)][ValidateRange(7,3650)][int]$MinimumImmutabilityDays,
    [Parameter(Mandatory)][ValidateRange(7,366)][int]$MinimumDailyRestorePoints,
    [Parameter(Mandatory)][ValidateRange(4,104)][int]$MinimumWeeklyRestorePoints,
    [Parameter(Mandatory)][ValidateRange(3,120)][int]$MinimumMonthlyRestorePoints,
    [Parameter(Mandatory)][string]$StorageObserverPrivateKeyFile,[Parameter(Mandatory)][string]$StorageObserverPublicKeyFile,
    [Parameter(Mandatory)][string]$StorageObserverKeyId,[Parameter(Mandatory)][string]$RequesterPrivateKeyFile,
    [Parameter(Mandatory)][string]$RequesterKeyId,[Parameter(Mandatory)][string]$ApproverPrivateKeyFile,
    [Parameter(Mandatory)][string]$ApproverKeyId,[Parameter(Mandatory)][string]$RequestedBy,[Parameter(Mandatory)][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1";. "$PSScriptRoot/BackupStorage.Common.ps1"
if($MinimumFailureDomains-gt$MinimumCopies){throw 'MinimumFailureDomains cannot exceed MinimumCopies'}
if($RequestedBy-ceq$ApprovedBy-or$RequesterKeyId-ceq$ApproverKeyId){throw 'Inventory requester and approver must be distinct'}
$policyRaw=& "$PSScriptRoot/Test-DisasterRecoveryPolicy.ps1" -PolicyFile $PolicyFile -RequesterPublicKeyFile $PolicyRequesterPublicKeyFile `
    -ExpectedRequesterKeyId $ExpectedPolicyRequesterKeyId -ApproverPublicKeyFile $PolicyApproverPublicKeyFile -ExpectedApproverKeyId $ExpectedPolicyApproverKeyId `
    -BackupSigningPublicKeyFile $BackupSigningPublicKeyFile -ExpectedBackupSigningKeyId $ExpectedBackupSigningKeyId `
    -DrillObserverPublicKeyFile $DrillObserverPublicKeyFile -ExpectedDrillObserverKeyId $ExpectedDrillObserverKeyId
$policy=($policyRaw-join[Environment]::NewLine)|ConvertFrom-Json
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath -TrustedPublicKeyFile $BackupSigningPublicKeyFile `
    -ExpectedSigningKeyId $ExpectedBackupSigningKeyId -ExpectedSourceDatabase $policy.Database
$backup=($backupRaw-join[Environment]::NewLine)|ConvertFrom-Json
$observerPrivate=Get-BackupKeyFingerprint $StorageObserverPrivateKeyFile 'Storage observer' $true
$observerPublic=Get-BackupKeyFingerprint $StorageObserverPublicKeyFile 'Storage observer' $false
$requester=Get-BackupKeyFingerprint $RequesterPrivateKeyFile 'Inventory requester' $true
$approver=Get-BackupKeyFingerprint $ApproverPrivateKeyFile 'Inventory approver' $true
$backupKey=Get-BackupKeyFingerprint $BackupSigningPublicKeyFile 'Backup signing' $false
$drillKey=Get-BackupKeyFingerprint $DrillObserverPublicKeyFile 'Drill observer' $false
if($observerPrivate-cne$observerPublic-or@(@($observerPublic,$requester,$approver,$backupKey,$drillKey)|Select-Object -Unique).Count-ne5){
    throw 'Storage inventory trust roles are not cryptographically separated'
}
$copies=Get-Content ([IO.Path]::GetFullPath($CopiesFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$retention=Get-Content ([IO.Path]::GetFullPath($RetentionFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$inventory=[ordered]@{SchemaVersion=1;Product='ET.Server';ArtifactType='BackupStorageInventory';InventoryId=$InventoryId;
    PolicyId=$policy.PolicyId;PolicySha256=$policy.PolicySha256;DeploymentId=$policy.DeploymentId;Database=$policy.Database;
    GeneratedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');Backup=[ordered]@{BackupId=$backup.BackupId;ManifestSha256=$backup.ManifestSha256;CreatedAtUtc=$backup.CreatedAtUtc};
    Requirements=[ordered]@{MinimumCopies=$MinimumCopies;MinimumFailureDomains=$MinimumFailureDomains;MinimumImmutabilityDays=$MinimumImmutabilityDays;
        MinimumDailyRestorePoints=$MinimumDailyRestorePoints;MinimumWeeklyRestorePoints=$MinimumWeeklyRestorePoints;MinimumMonthlyRestorePoints=$MinimumMonthlyRestorePoints};
    Copies=@($copies);Retention=$retention;StorageObserverKeyId=$StorageObserverKeyId;StorageObserverPublicKeySha256=$observerPublic;
    RequestedBy=$RequestedBy;ApprovedBy=$ApprovedBy;RequesterKeyId=$RequesterKeyId;ApproverKeyId=$ApproverKeyId}
Assert-BackupStorageInventory $inventory $policy $backup ([DateTimeOffset]::UtcNow)
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory';New-Item -ItemType Directory $output -Force|Out-Null
$file=Join-Path $output "$InventoryId.backup-inventory.json";if((Test-Path $file)-or(Test-Path "$file.observer.sig")-or(Test-Path "$file.requester.sig")-or(Test-Path "$file.approver.sig")){throw 'Backup inventory already exists'}
$bytes=$script:BackupRestoreUtf8.GetBytes(($inventory|ConvertTo-Json -Depth 10))
try{[IO.File]::WriteAllBytes($file,$bytes);[IO.File]::WriteAllBytes("$file.observer.sig",(New-BackupSignature $bytes $StorageObserverPrivateKeyFile 'Storage observer'));
    [IO.File]::WriteAllBytes("$file.requester.sig",(New-BackupSignature $bytes $RequesterPrivateKeyFile 'Inventory requester'));
    [IO.File]::WriteAllBytes("$file.approver.sig",(New-BackupSignature $bytes $ApproverPrivateKeyFile 'Inventory approver'));
    [ordered]@{Passed=$true;InventoryFile=$file;InventoryId=$InventoryId;Copies=@($copies).Count;
        FailureDomains=@($copies.FailureDomain|Select-Object -Unique).Count;InventorySha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}catch{foreach($path in @($file,"$file.observer.sig","$file.requester.sig","$file.approver.sig")){if(Test-Path $path){Remove-Item $path -Force}};throw}

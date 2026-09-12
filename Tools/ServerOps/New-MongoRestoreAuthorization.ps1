param(
    [Parameter(Mandatory)][string]$BackupPath,[Parameter(Mandatory)][string]$BackupTrustedPublicKeyFile,
    [Parameter(Mandatory)][string]$BackupExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$TargetDatabase,
    [Parameter(Mandatory)][string]$ConfirmTargetDatabase,[switch]$DropExisting,
    [Parameter(Mandatory)][string]$ConfirmRestoreMode,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$OperationId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._@-]{3,80}$')][string]$RequestedBy,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._@-]{3,80}$')][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$RequesterPrivateKeyFile,[Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$RequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPrivateKeyFile,[Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ApproverKeyId,
    [Parameter(Mandatory)][string]$OutputDirectory,[ValidateRange(5,240)][int]$ValidForMinutes=30
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"
if($ConfirmTargetDatabase -cne $TargetDatabase){throw 'ConfirmTargetDatabase must exactly match TargetDatabase'}
$expectedMode=$(if($DropExisting){"DROP $TargetDatabase"}else{"NO DROP $TargetDatabase"})
if($ConfirmRestoreMode -cne $expectedMode){throw 'ConfirmRestoreMode does not exactly confirm the restore overwrite mode'}
if($RequestedBy -ceq $ApprovedBy -or $RequesterKeyId -ceq $ApproverKeyId){throw 'Restore requester and approver must be distinct'}
$backupRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" -BackupPath $BackupPath `
    -TrustedPublicKeyFile $BackupTrustedPublicKeyFile -ExpectedSigningKeyId $BackupExpectedSigningKeyId
$backup=($backupRaw -join [Environment]::NewLine)|ConvertFrom-Json
$requesterFingerprint=Get-BackupKeyFingerprint $RequesterPrivateKeyFile 'Restore requester' $true
$approverFingerprint=Get-BackupKeyFingerprint $ApproverPrivateKeyFile 'Restore approver' $true
$backupFingerprint=Get-BackupKeyFingerprint $BackupTrustedPublicKeyFile 'Backup' $false
if($requesterFingerprint -in @($approverFingerprint,$backupFingerprint) -or $approverFingerprint -eq $backupFingerprint){
    throw 'Backup, restore requester, and restore approver keys must be cryptographically distinct'
}
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory'
$backupRoot=[IO.Path]::GetFullPath($BackupPath).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(($output+[IO.Path]::DirectorySeparatorChar).StartsWith($backupRoot,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Restore authorization output cannot be inside the signed backup artifact'
}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$file=Join-Path $output "$OperationId.restore.json"
if((Test-Path $file)-or(Test-Path "$file.requester.sig")-or(Test-Path "$file.approver.sig")){throw 'Restore authorization already exists'}
$created=[DateTimeOffset]::UtcNow
$authorization=[ordered]@{SchemaVersion=1;Product='ET.Server';Action='MongoRestore';OperationId=$OperationId;
    CreatedAtUtc=$created.ToString('O');ExpiresAtUtc=$created.AddMinutes($ValidForMinutes).ToString('O');
    RequestedBy=$RequestedBy;ApprovedBy=$ApprovedBy;RequesterKeyId=$RequesterKeyId;ApproverKeyId=$ApproverKeyId;
    Backup=[ordered]@{BackupId=$backup.BackupId;SourceDatabase=$backup.SourceDatabase;ManifestSha256=$backup.ManifestSha256};
    TargetDatabase=$TargetDatabase;DropExisting=[bool]$DropExisting}
$bytes=$script:BackupRestoreUtf8.GetBytes(($authorization|ConvertTo-Json -Depth 8))
try{
    [IO.File]::WriteAllBytes($file,$bytes)
    [IO.File]::WriteAllBytes("$file.requester.sig",(New-BackupSignature $bytes $RequesterPrivateKeyFile 'Restore requester'))
    [IO.File]::WriteAllBytes("$file.approver.sig",(New-BackupSignature $bytes $ApproverPrivateKeyFile 'Restore approver'))
    [ordered]@{Passed=$true;AuthorizationFile=$file;OperationId=$OperationId;BackupId=$backup.BackupId;
        TargetDatabase=$TargetDatabase;DropExisting=[bool]$DropExisting;ExpiresAtUtc=$authorization.ExpiresAtUtc}|ConvertTo-Json
}catch{
    foreach($path in @($file,"$file.requester.sig","$file.approver.sig")){if(Test-Path $path){Remove-Item $path -Force}}
    throw
}

param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$PolicyId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$DeploymentId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Database,
    [Parameter(Mandatory)][ValidateRange(1,1440)][int]$RpoMinutes,
    [Parameter(Mandatory)][ValidateRange(1,1440)][int]$RtoMinutes,
    [Parameter(Mandatory)][ValidateRange(1,1440)][int]$BackupIntervalMinutes,
    [Parameter(Mandatory)][ValidateRange(1,180)][int]$MaximumDrillAgeDays,
    [Parameter(Mandatory)][string]$BackupSigningPublicKeyFile,
    [Parameter(Mandatory)][string]$BackupSigningKeyId,
    [Parameter(Mandatory)][string]$DrillObserverPublicKeyFile,
    [Parameter(Mandatory)][string]$DrillObserverKeyId,
    [Parameter(Mandatory)][string]$RequesterPrivateKeyFile,[Parameter(Mandatory)][string]$RequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPrivateKeyFile,[Parameter(Mandatory)][string]$ApproverKeyId,
    [Parameter(Mandatory)][string]$RequestedBy,[Parameter(Mandatory)][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$OutputDirectory,[ValidateRange(1,365)][int]$ReviewAfterDays=90
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1"
if($BackupIntervalMinutes-gt$RpoMinutes){throw 'Backup interval cannot exceed RPO'}
if($RequestedBy-ceq$ApprovedBy-or$RequesterKeyId-ceq$ApproverKeyId){throw 'Policy requester and approver must be distinct'}
$requester=Get-BackupKeyFingerprint $RequesterPrivateKeyFile 'Policy requester' $true
$approver=Get-BackupKeyFingerprint $ApproverPrivateKeyFile 'Policy approver' $true
$backup=Get-BackupKeyFingerprint $BackupSigningPublicKeyFile 'Backup signing' $false
$observer=Get-BackupKeyFingerprint $DrillObserverPublicKeyFile 'Drill observer' $false
if(@(@($requester,$approver,$backup,$observer)|Select-Object -Unique).Count-ne4){throw 'Policy, backup, and drill observer keys must all be distinct'}
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory';New-Item -ItemType Directory $output -Force|Out-Null
$file=Join-Path $output "$PolicyId.dr-policy.json"
if((Test-Path $file)-or(Test-Path "$file.requester.sig")-or(Test-Path "$file.approver.sig")){throw 'Disaster recovery policy already exists'}
$now=[DateTimeOffset]::UtcNow
$policy=[ordered]@{SchemaVersion=1;Product='ET.Server';PolicyId=$PolicyId;DeploymentId=$DeploymentId;Database=$Database;
    EffectiveAtUtc=$now.ToString('O');ReviewDueUtc=$now.AddDays($ReviewAfterDays).ToString('O');
    RpoMinutes=$RpoMinutes;RtoMinutes=$RtoMinutes;BackupIntervalMinutes=$BackupIntervalMinutes;MaximumDrillAgeDays=$MaximumDrillAgeDays;
    BackupSigningKeyId=$BackupSigningKeyId;BackupSigningPublicKeySha256=$backup;
    DrillObserverKeyId=$DrillObserverKeyId;DrillObserverPublicKeySha256=$observer;
    RequestedBy=$RequestedBy;ApprovedBy=$ApprovedBy;RequesterKeyId=$RequesterKeyId;ApproverKeyId=$ApproverKeyId}
$bytes=$script:BackupRestoreUtf8.GetBytes(($policy|ConvertTo-Json -Depth 6))
try{
    [IO.File]::WriteAllBytes($file,$bytes)
    [IO.File]::WriteAllBytes("$file.requester.sig",(New-BackupSignature $bytes $RequesterPrivateKeyFile 'Policy requester'))
    [IO.File]::WriteAllBytes("$file.approver.sig",(New-BackupSignature $bytes $ApproverPrivateKeyFile 'Policy approver'))
    [ordered]@{Passed=$true;PolicyFile=$file;PolicyId=$PolicyId;PolicySha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant();
        RpoMinutes=$RpoMinutes;RtoMinutes=$RtoMinutes;ReviewDueUtc=$policy.ReviewDueUtc}|ConvertTo-Json
}catch{foreach($path in @($file,"$file.requester.sig","$file.approver.sig")){if(Test-Path $path){Remove-Item $path -Force}};throw}

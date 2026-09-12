param(
    [Parameter(Mandatory)][string]$RecordId,[Parameter(Mandatory)][string]$ChangeId,[Parameter(Mandatory)][string]$DeploymentId,
    [Parameter(Mandatory)][ValidateSet('Staging','Production')][string]$Environment,
    [Parameter(Mandatory)][ValidateSet('Low','Medium','High','Emergency')][string]$RiskLevel,
    [Parameter(Mandatory)][DateTimeOffset]$WindowStartUtc,[Parameter(Mandatory)][DateTimeOffset]$WindowEndUtc,
    [Parameter(Mandatory)][string]$ChecklistFile,[Parameter(Mandatory)][string]$RequestedBy,[Parameter(Mandatory)][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$RequesterPrivateKeyFile,[Parameter(Mandatory)][string]$RequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPrivateKeyFile,[Parameter(Mandatory)][string]$ApproverKeyId,
    [Parameter(Mandatory)][string]$ReleaseSigningPublicKeyFile,[Parameter(Mandatory)][string]$DeploymentStatePublicKeyFile,
    [Parameter(Mandatory)][string]$OutputDirectory,[switch]$TestOnlyRecord
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1";. "$PSScriptRoot/ServerChange.Common.ps1"
if($RequestedBy-ceq$ApprovedBy-or$RequesterKeyId-ceq$ApproverKeyId){throw 'Change requester and approver must be distinct'}
$requester=Get-BackupKeyFingerprint $RequesterPrivateKeyFile 'Change requester' $true
$approver=Get-BackupKeyFingerprint $ApproverPrivateKeyFile 'Change approver' $true
$release=Get-BackupKeyFingerprint $ReleaseSigningPublicKeyFile 'Release signer' $false
$state=Get-BackupKeyFingerprint $DeploymentStatePublicKeyFile 'Deployment state signer' $false
if(@(@($requester,$approver,$release,$state)|Select-Object -Unique).Count-ne4){throw 'Change approval and deployment trust roles are not cryptographically separated'}
$checklist=Get-Content ([IO.Path]::GetFullPath($ChecklistFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$items=@();foreach($source in @($checklist)){
    $evidence=[IO.Path]::GetFullPath([string]$source.EvidenceFile)
    if(-not(Test-Path -LiteralPath $evidence -PathType Leaf)){throw "Change evidence is missing: $($source.ItemId)"}
    if(-not$TestOnlyRecord){Assert-BackupPathOutsideRepository $evidence "Evidence $($source.ItemId)"|Out-Null}
    $items+=[ordered]@{ItemId=[string]$source.ItemId;Status=[string]$source.Status;EvidenceFile=$evidence;
        EvidenceSha256=(Get-FileHash -LiteralPath $evidence -Algorithm SHA256).Hash.ToLowerInvariant();
        ObservedAtUtc=[DateTimeOffset]::Parse([string]$source.ObservedAtUtc).ToString('O');ValidUntilUtc=[DateTimeOffset]::Parse([string]$source.ValidUntilUtc).ToString('O')}
}
$record=[ordered]@{SchemaVersion=1;Product='ET.Server';ArtifactType='ServerChangeRecord';RecordId=$RecordId;ChangeId=$ChangeId;
    DeploymentId=$DeploymentId;Environment=$Environment;RiskLevel=$RiskLevel;Decision='APPROVED';TestOnly=[bool]$TestOnlyRecord;
    CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');WindowStartUtc=$WindowStartUtc.ToUniversalTime().ToString('O');WindowEndUtc=$WindowEndUtc.ToUniversalTime().ToString('O');
    RequestedBy=$RequestedBy;ApprovedBy=$ApprovedBy;RequesterKeyId=$RequesterKeyId;ApproverKeyId=$ApproverKeyId;
    RequesterPublicKeySha256=$requester;ApproverPublicKeySha256=$approver;ReleaseSigningPublicKeySha256=$release;
    DeploymentStatePublicKeySha256=$state;Checklist=$items}
Assert-ServerChangeRecord $record ([DateTimeOffset]::UtcNow) ([bool]$TestOnlyRecord) $false
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory';New-Item -ItemType Directory $output -Force|Out-Null
$file=Join-Path $output "$RecordId.server-change.json";if((Test-Path $file)-or(Test-Path "$file.requester.sig")-or(Test-Path "$file.approver.sig")){throw 'Server change record already exists'}
$bytes=$script:ServerChangeUtf8.GetBytes(($record|ConvertTo-Json -Depth 10))
try{
    [IO.File]::WriteAllBytes($file,$bytes)
    [IO.File]::WriteAllBytes("$file.requester.sig",(New-BackupSignature $bytes $RequesterPrivateKeyFile 'Change requester'))
    [IO.File]::WriteAllBytes("$file.approver.sig",(New-BackupSignature $bytes $ApproverPrivateKeyFile 'Change approver'))
    [ordered]@{Passed=$true;RecordFile=$file;RecordId=$RecordId;ChangeId=$ChangeId;ChecklistItems=$items.Count;
        RecordSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}catch{foreach($path in @($file,"$file.requester.sig","$file.approver.sig")){if(Test-Path $path){Remove-Item $path -Force}};throw}

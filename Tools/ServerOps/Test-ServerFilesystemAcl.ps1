param(
    [Parameter(Mandatory)][string]$ReleaseRoot,[Parameter(Mandatory)][string]$RuntimeRoot,
    [Parameter(Mandatory)][string]$DeploymentStateRoot,[Parameter(Mandatory)][string]$SecretRoot,
    [Parameter(Mandatory)][string]$ServiceIdentity,[Parameter(Mandatory)][string[]]$PrivateKeyFiles
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1"
# 把账号或服务身份解析为 SID。
function Resolve-Sid([string]$Identity){
    try{if($Identity-match'^S-1-'){return [Security.Principal.SecurityIdentifier]::new($Identity)}
        return ([Security.Principal.NTAccount]::new($Identity)).Translate([Security.Principal.SecurityIdentifier])}
    catch{throw "Windows identity cannot be resolved: $Identity"}
}
# 汇总指定主体在路径上的有效权限。
function Get-PrincipalRights([string]$Path,[Security.Principal.SecurityIdentifier]$Sid){
    $allow=[Security.AccessControl.FileSystemRights]0;$deny=[Security.AccessControl.FileSystemRights]0;$acl=Get-Acl -LiteralPath $Path
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.IdentityReference.Value-ceq$Sid.Value){if($rule.AccessControlType-eq[Security.AccessControl.AccessControlType]::Allow){$allow=$allow-bor$rule.FileSystemRights}else{$deny=$deny-bor$rule.FileSystemRights}}
    };return @{Allow=$allow;Deny=$deny}
}
# 确认主体不拥有被禁止的文件权限。
function Assert-NoRights([string]$Path,[Security.Principal.SecurityIdentifier[]]$Sids,[Security.AccessControl.FileSystemRights]$Mask,[string]$Reason){
    foreach($sid in $Sids){$rights=Get-PrincipalRights $Path $sid;if(($rights.Allow-band$Mask)-ne0){throw "$Reason ($($sid.Value))"}}
}
# 确认主体拥有运行所需的最小权限。
function Assert-HasRights([string]$Path,[Security.Principal.SecurityIdentifier]$Sid,[Security.AccessControl.FileSystemRights]$Mask,[string]$Reason){
    $rights=Get-PrincipalRights $Path $Sid;if(($rights.Allow-band$Mask)-ne$Mask-or($rights.Deny-band$Mask)-ne0){throw $Reason};return $rights.Allow
}
# 确认路径及其子项不包含重解析点。
function Assert-NoReparse([string]$Path,[bool]$Recursive){
    $item=Get-Item -LiteralPath $Path -Force;if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Reparse point is forbidden in protected path: $Path"}
    if($Recursive){foreach($child in Get-ChildItem -LiteralPath $Path -Force -Recurse){if(($child.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Reparse point is forbidden in protected tree: $($child.FullName)"}}}
}
$release=Assert-BackupPathOutsideRepository $ReleaseRoot 'ReleaseRoot';$runtime=Assert-BackupPathOutsideRepository $RuntimeRoot 'RuntimeRoot'
$state=Assert-BackupPathOutsideRepository $DeploymentStateRoot 'DeploymentStateRoot';$secret=Assert-BackupPathOutsideRepository $SecretRoot 'SecretRoot'
foreach($path in @($release,$runtime,$state,$secret)){if(-not(Test-Path $path -PathType Container)){throw "Protected directory does not exist: $path"}}
$prefix=$release.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar;if(($runtime+[IO.Path]::DirectorySeparatorChar).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or($state+[IO.Path]::DirectorySeparatorChar).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or($secret+[IO.Path]::DirectorySeparatorChar).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Writable state, runtime or secrets cannot be beneath immutable releases'}
$service=Resolve-Sid $ServiceIdentity;if($service.Value-notmatch'^S-1-5-80-(\d+-){4}\d+$'){throw 'ServiceIdentity must be a dedicated NT SERVICE virtual account SID'}
$broad=@('S-1-1-0','S-1-5-11','S-1-5-32-545')|ForEach-Object {[Security.Principal.SecurityIdentifier]::new($_)}
$write=[Security.AccessControl.FileSystemRights]::Write-bor[Security.AccessControl.FileSystemRights]::Delete-bor[Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles-bor[Security.AccessControl.FileSystemRights]::ChangePermissions-bor[Security.AccessControl.FileSystemRights]::TakeOwnership
$read=[Security.AccessControl.FileSystemRights]::Read-bor[Security.AccessControl.FileSystemRights]::ReadAndExecute-bor[Security.AccessControl.FileSystemRights]::ReadData
Assert-NoReparse $release $true;Assert-NoReparse $runtime $false;Assert-NoReparse $state $true;Assert-NoReparse $secret $true
Assert-NoRights $release $broad $write 'Broad principal can modify immutable releases';Assert-NoRights $runtime $broad $write 'Broad principal can modify runtime data';Assert-NoRights $state $broad $write 'Broad principal can modify deployment state';Assert-NoRights $secret $broad $read 'Broad principal can read secrets'
$releaseRights=Assert-HasRights $release $service ([Security.AccessControl.FileSystemRights]::ReadAndExecute) 'Service identity cannot read and execute releases';if(($releaseRights-band$write)-ne0){throw 'Service identity can modify immutable releases'}
Assert-HasRights $runtime $service ([Security.AccessControl.FileSystemRights]::Modify) 'Service identity cannot modify runtime data'|Out-Null
Assert-HasRights $state $service ([Security.AccessControl.FileSystemRights]::Modify) 'Service identity cannot update deployment state'|Out-Null
$secretRights=Assert-HasRights $secret $service ([Security.AccessControl.FileSystemRights]::Read) 'Service identity cannot read secrets';if(($secretRights-band$write)-ne0){throw 'Service identity can modify secrets'}
$keyResults=@();foreach($key in $PrivateKeyFiles){$file=[IO.Path]::GetFullPath($key);if(-not(Test-Path $file -PathType Leaf)){throw "Private key does not exist: $file"};if(-not(($file+[IO.Path]::DirectorySeparatorChar).StartsWith($secret.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){throw 'Private keys must be beneath SecretRoot'};Assert-NoReparse $file $false;Assert-NoRights $file $broad $read 'Broad principal can read a private key';$rights=Assert-HasRights $file $service ([Security.AccessControl.FileSystemRights]::Read) 'Service identity cannot read a private key';if(($rights-band$write)-ne0){throw 'Service identity can modify a private key'};$keyResults+=$file}
[ordered]@{Passed=$true;ServiceSid=$service.Value;ReleaseReadOnly=$true;RuntimeWritable=$true;DeploymentStateWritable=$true;SecretReadOnly=$true;PrivateKeysVerified=$keyResults.Count;BroadPrincipalsRejected=$true;ReparsePointsRejected=$true}|ConvertTo-Json

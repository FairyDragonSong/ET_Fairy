param(
    [Parameter(Mandatory)][string]$CurrentReleaseDirectory,
    [Parameter(Mandatory)][string]$CandidateReleaseDirectory,
    [Parameter(Mandatory)][string]$CurrentTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CurrentExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$CandidateTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CandidateExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$ChangeId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._@-]{3,80}$')][string]$RequestedBy,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._@-]{3,80}$')][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$RequesterPrivateKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$RequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPrivateKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ApproverKeyId,
    [Parameter(Mandatory)][string]$MonitoringPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$MonitoringKeyId,
    [Parameter(Mandatory)][string]$ConfirmCandidateVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,25)][int]$CanaryPercent=5,
    [ValidateRange(5,1440)][int]$ObservationMinutes=15,
    [ValidateRange(0,10)][decimal]$MaximumErrorRatePercent=1,
    [ValidateRange(90,100)][decimal]$MinimumReadyPercent=99,
    [ValidateRange(1,60000)][int]$MaximumP95Milliseconds=1000,
    [ValidateRange(30,1440)][int]$ValidForMinutes=240,
    [switch]$TestOnlyPlan,
    [switch]$AllowDirtyTestArtifacts
)

$ErrorActionPreference='Stop'
if($RequestedBy -eq $ApprovedBy -or $RequesterKeyId -eq $ApproverKeyId){
    throw 'Requester and approver must be distinct identities and keys'
}
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
# 解析仓库外部的签名私钥。
function Resolve-ExternalPrivateKey([string]$Path,[string]$Name){
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not(Test-Path -LiteralPath $resolved -PathType Leaf)){throw "$Name private key does not exist"}
    $repoPrefix=$repositoryRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if($resolved.StartsWith($repoPrefix,[StringComparison]::OrdinalIgnoreCase)){throw "$Name private key must be outside the repository"}
    return $resolved
}
$requesterKey=Resolve-ExternalPrivateKey $RequesterPrivateKeyFile 'Requester'
$approverKey=Resolve-ExternalPrivateKey $ApproverPrivateKeyFile 'Approver'
if($requesterKey -eq $approverKey){throw 'Requester and approver must not share a private key file'}
# 从私钥导出公钥并计算指纹。
function Get-PrivatePublicFingerprint([string]$Path,[string]$Name){
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($Path))
        if($rsa.KeySize -lt 3072){throw "$Name approval key must be at least RSA-3072"}
        $rsa.ExportParameters($true)|Out-Null
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}
# 读取公钥并计算稳定指纹。
function Get-PublicFingerprint([string]$Path,[string]$Name){
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not(Test-Path -LiteralPath $resolved -PathType Leaf)){throw "$Name public key does not exist"}
    $pem=[IO.File]::ReadAllText($resolved)
    if($pem -match 'PRIVATE KEY'){throw "$Name trust input must be a public key"}
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem($pem)
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}
$requesterFingerprint=Get-PrivatePublicFingerprint $requesterKey 'Requester'
$approverFingerprint=Get-PrivatePublicFingerprint $approverKey 'Approver'
$releaseFingerprints=@(
    Get-PublicFingerprint $CurrentTrustedPublicKeyFile 'Current release'
    Get-PublicFingerprint $CandidateTrustedPublicKeyFile 'Candidate release'
)
$monitoringFingerprint=Get-PublicFingerprint $MonitoringPublicKeyFile 'Monitoring'
if($requesterFingerprint -eq $approverFingerprint){throw 'Requester and approver keys must be cryptographically distinct'}
if($requesterFingerprint -in $releaseFingerprints -or $approverFingerprint -in $releaseFingerprints){
    throw 'Rollout approval keys must be separate from release signing keys'
}
$nonMonitoringFingerprints=@($requesterFingerprint,$approverFingerprint)+$releaseFingerprints
if($monitoringFingerprint -in $nonMonitoringFingerprints){
    throw 'Monitoring key must be separate from release and approval keys'
}

$compatibilityParameters=@{
    CurrentReleaseDirectory=$CurrentReleaseDirectory
    CandidateReleaseDirectory=$CandidateReleaseDirectory
    CurrentTrustedPublicKeyFile=$CurrentTrustedPublicKeyFile
    CurrentExpectedSigningKeyId=$CurrentExpectedSigningKeyId
    CandidateTrustedPublicKeyFile=$CandidateTrustedPublicKeyFile
    CandidateExpectedSigningKeyId=$CandidateExpectedSigningKeyId
    ExpectedPackageSupplier=$ExpectedPackageSupplier
    ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase
}
if($AllowDirtyTestArtifacts){$compatibilityParameters.AllowDirtyTestArtifacts=$true}
$raw=& "$PSScriptRoot/Test-ServerReleaseCompatibility.ps1" @compatibilityParameters
try{$compatibility=($raw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12}
catch{throw 'Compatibility verifier did not return valid JSON'}
if(-not $compatibility.Passed -or -not $compatibility.RollbackSafe -or -not $compatibility.CanarySafe){
    throw 'Release compatibility did not approve canary and rollback'
}
if($ConfirmCandidateVersion -cne $compatibility.Candidate.Version){throw 'ConfirmCandidateVersion does not match verified candidate'}

$output=[IO.Path]::GetFullPath($OutputDirectory)
if($output.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($output).TrimEnd('\','/')){throw 'OutputDirectory cannot be a drive root'}
$currentRoot=[IO.Path]::GetFullPath($CurrentReleaseDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$candidateRoot=[IO.Path]::GetFullPath($CandidateReleaseDirectory).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(($output.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar).StartsWith($currentRoot,[StringComparison]::OrdinalIgnoreCase) -or
   ($output.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar).StartsWith($candidateRoot,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Rollout plan output must be separate from immutable release directories'
}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$planPath=Join-Path $output "$ChangeId.rollout.json"
$requesterSignaturePath="$planPath.requester.sig"
$approverSignaturePath="$planPath.approver.sig"
foreach($path in @($planPath,$requesterSignaturePath,$approverSignaturePath)){
    if(Test-Path -LiteralPath $path){throw 'Rollout plan output already exists; refusing to overwrite'}
}

$created=[DateTimeOffset]::UtcNow
$waves=@($CanaryPercent,25,50,100|Sort-Object -Unique)
$plan=[ordered]@{
    SchemaVersion=1
    Product='ET.Server'
    ChangeId=$ChangeId
    CreatedAtUtc=$created.ToString('O')
    ExpiresAtUtc=$created.AddMinutes($ValidForMinutes).ToString('O')
    RequestedBy=$RequestedBy
    ApprovedBy=$ApprovedBy
    RequesterKeyId=$RequesterKeyId
    ApproverKeyId=$ApproverKeyId
    MonitoringKeyId=$MonitoringKeyId
    MonitoringPublicKeySha256=$monitoringFingerprint
    Current=$compatibility.Current
    Candidate=$compatibility.Candidate
    Compatibility=[ordered]@{Database=$compatibility.Database;Protocol=$compatibility.Protocol;
        RollbackSafe=[bool]$compatibility.RollbackSafe;CanarySafe=[bool]$compatibility.CanarySafe}
    Rollout=[ordered]@{WavesPercent=$waves;ObservationMinutes=$ObservationMinutes;
        MaximumErrorRatePercent=$MaximumErrorRatePercent;MinimumReadyPercent=$MinimumReadyPercent;
        MaximumP95Milliseconds=$MaximumP95Milliseconds;RollbackOnCrash=$true;
        RollbackOnJournalFailure=$true;RollbackOnSaveFailure=$true}
    Rollback=[ordered]@{Version=$compatibility.Current.Version;SourceCommit=$compatibility.Current.SourceCommit;
        ArchiveSha256=$compatibility.Current.ArchiveSha256;AutomaticDecisionRequired=$true}
    ProductionApproved=(-not $AllowDirtyTestArtifacts -and -not $TestOnlyPlan)
}
$utf8=[Text.UTF8Encoding]::new($false)
$planBytes=$utf8.GetBytes(($plan|ConvertTo-Json -Depth 10))
# 为发布计划生成不可篡改签名。
function Sign-Plan([string]$PrivateKeyPath){
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($PrivateKeyPath))
        return $rsa.SignData($planBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally{$rsa.Dispose()}
}
try{
    [IO.File]::WriteAllBytes($planPath,$planBytes)
    [IO.File]::WriteAllBytes($requesterSignaturePath,(Sign-Plan $requesterKey))
    [IO.File]::WriteAllBytes($approverSignaturePath,(Sign-Plan $approverKey))
    [ordered]@{Passed=$true;Plan=$planPath;ChangeId=$ChangeId;CurrentVersion=$compatibility.Current.Version;
        CandidateVersion=$compatibility.Candidate.Version;ProductionApproved=$plan.ProductionApproved;
        PlanSha256=(Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}
catch{
    foreach($path in @($planPath,$requesterSignaturePath,$approverSignaturePath)){
        if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}
    }
    throw
}

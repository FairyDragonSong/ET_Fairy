param(
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedRequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedApproverKeyId,
    [Parameter(Mandatory)][string]$MonitoringPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedMonitoringKeyId,
    [Parameter(Mandatory)][string]$CurrentReleaseDirectory,
    [Parameter(Mandatory)][string]$CandidateReleaseDirectory,
    [Parameter(Mandatory)][string]$CurrentTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CurrentExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$CandidateTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CandidateExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [switch]$AllowTestPlan,
    [switch]$AllowDirtyTestArtifacts
)

$ErrorActionPreference='Stop'
$resolvedPlan=[IO.Path]::GetFullPath($PlanFile)
if(-not(Test-Path -LiteralPath $resolvedPlan -PathType Leaf)){throw 'Rollout plan does not exist'}
$requesterSignature="$resolvedPlan.requester.sig"
$approverSignature="$resolvedPlan.approver.sig"
if(-not(Test-Path -LiteralPath $requesterSignature -PathType Leaf) -or
   -not(Test-Path -LiteralPath $approverSignature -PathType Leaf)){
    throw 'Rollout plan requires requester and approver signatures'
}
$planBytes=[IO.File]::ReadAllBytes($resolvedPlan)
try{$plan=[Text.Encoding]::UTF8.GetString($planBytes)|ConvertFrom-Json -Depth 15 -DateKind String}
catch{throw 'Rollout plan is not valid JSON'}

function Test-Signature([string]$PublicKeyFile,[string]$SignatureFile,[string]$Name){
    $path=[IO.Path]::GetFullPath($PublicKeyFile)
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "$Name public key does not exist"}
    $pem=[IO.File]::ReadAllText($path)
    if($pem -match 'PRIVATE KEY'){throw "$Name verifier requires a public key"}
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem($pem)
        if($rsa.KeySize -lt 3072){throw "$Name public key must be at least RSA-3072"}
        if(-not $rsa.VerifyData($planBytes,[IO.File]::ReadAllBytes($SignatureFile),
            [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){
            throw "$Name rollout signature is not trusted"
        }
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}
$requesterFingerprint=Test-Signature $RequesterPublicKeyFile $requesterSignature 'Requester'
$approverFingerprint=Test-Signature $ApproverPublicKeyFile $approverSignature 'Approver'
if($requesterFingerprint -eq $approverFingerprint){throw 'Requester and approver public keys are identical'}
function Get-TrustFingerprint([string]$PublicKeyFile,[string]$Name){
    $pem=[IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyFile))
    if($pem -match 'PRIVATE KEY'){throw "$Name trust input must be a public key"}
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem($pem)
        if($rsa.KeySize -lt 3072){throw "$Name public key must be at least RSA-3072"}
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}
$releaseFingerprints=@(
    Get-TrustFingerprint $CurrentTrustedPublicKeyFile 'Current release'
    Get-TrustFingerprint $CandidateTrustedPublicKeyFile 'Candidate release'
)
$monitoringFingerprint=Get-TrustFingerprint $MonitoringPublicKeyFile 'Monitoring'
if($requesterFingerprint -in $releaseFingerprints -or $approverFingerprint -in $releaseFingerprints){
    throw 'Rollout approval keys are not separated from release signing trust'
}
$nonMonitoringFingerprints=@($requesterFingerprint,$approverFingerprint)+$releaseFingerprints
if($monitoringFingerprint -in $nonMonitoringFingerprints){
    throw 'Monitoring key is not separated from release and approval trust'
}

$created=[DateTimeOffset]::MinValue
$expires=[DateTimeOffset]::MinValue
if($plan.SchemaVersion -ne 1 -or $plan.Product -ne 'ET.Server' -or $plan.ChangeId -notmatch '^[0-9A-Za-z._-]{3,80}$' -or
   $plan.RequestedBy -notmatch '^[0-9A-Za-z._@-]{3,80}$' -or $plan.ApprovedBy -notmatch '^[0-9A-Za-z._@-]{3,80}$' -or
   $plan.RequestedBy -eq $plan.ApprovedBy -or $plan.RequesterKeyId -ne $ExpectedRequesterKeyId -or
   $plan.ApproverKeyId -ne $ExpectedApproverKeyId -or $ExpectedRequesterKeyId -eq $ExpectedApproverKeyId -or
   $plan.MonitoringKeyId -ne $ExpectedMonitoringKeyId -or $plan.MonitoringPublicKeySha256 -ne $monitoringFingerprint -or
   -not [DateTimeOffset]::TryParse([string]$plan.CreatedAtUtc,[ref]$created) -or
   -not [DateTimeOffset]::TryParse([string]$plan.ExpiresAtUtc,[ref]$expires) -or
   $created -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or $expires -le $created -or
   $expires -gt $created.AddHours(24) -or $expires -lt [DateTimeOffset]::UtcNow){
    throw 'Rollout plan identity or validity window is invalid'
}
if(-not $AllowTestPlan -and -not [bool]$plan.ProductionApproved){throw 'Test-only rollout plan is not accepted for production'}
$waves=@($plan.Rollout.WavesPercent)
if($waves.Count -lt 2 -or $waves.Count -gt 4 -or [int]$waves[0] -lt 1 -or [int]$waves[0] -gt 25 -or
   [int]$waves[-1] -ne 100 -or ($waves|Select-Object -Unique).Count -ne $waves.Count){
    throw 'Rollout waves are invalid'
}
for($index=1;$index-lt$waves.Count;$index++){if([int]$waves[$index]-le[int]$waves[$index-1]){throw 'Rollout waves must increase'}}
if([int]$plan.Rollout.ObservationMinutes -lt 5 -or [int]$plan.Rollout.ObservationMinutes -gt 1440 -or
   [decimal]$plan.Rollout.MaximumErrorRatePercent -lt 0 -or [decimal]$plan.Rollout.MaximumErrorRatePercent -gt 10 -or
   [decimal]$plan.Rollout.MinimumReadyPercent -lt 90 -or [decimal]$plan.Rollout.MinimumReadyPercent -gt 100 -or
   [int]$plan.Rollout.MaximumP95Milliseconds -lt 1 -or [int]$plan.Rollout.MaximumP95Milliseconds -gt 60000 -or
   -not [bool]$plan.Rollout.RollbackOnCrash -or -not [bool]$plan.Rollout.RollbackOnJournalFailure -or
   -not [bool]$plan.Rollout.RollbackOnSaveFailure -or -not [bool]$plan.Rollback.AutomaticDecisionRequired){
    throw 'Rollout health thresholds or rollback policy are invalid'
}

$compatibilityParameters=@{
    CurrentReleaseDirectory=$CurrentReleaseDirectory;CandidateReleaseDirectory=$CandidateReleaseDirectory
    CurrentTrustedPublicKeyFile=$CurrentTrustedPublicKeyFile;CurrentExpectedSigningKeyId=$CurrentExpectedSigningKeyId
    CandidateTrustedPublicKeyFile=$CandidateTrustedPublicKeyFile;CandidateExpectedSigningKeyId=$CandidateExpectedSigningKeyId
    ExpectedPackageSupplier=$ExpectedPackageSupplier;ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase
}
if($AllowDirtyTestArtifacts){$compatibilityParameters.AllowDirtyTestArtifacts=$true}
$raw=& "$PSScriptRoot/Test-ServerReleaseCompatibility.ps1" @compatibilityParameters
try{$verified=($raw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12}
catch{throw 'Compatibility verifier did not return valid JSON'}
if($plan.Current.Version -ne $verified.Current.Version -or $plan.Current.SourceCommit -ne $verified.Current.SourceCommit -or
   $plan.Current.ArchiveSha256 -ne $verified.Current.ArchiveSha256 -or
   $plan.Candidate.Version -ne $verified.Candidate.Version -or $plan.Candidate.SourceCommit -ne $verified.Candidate.SourceCommit -or
   $plan.Candidate.ArchiveSha256 -ne $verified.Candidate.ArchiveSha256 -or
   $plan.Rollback.Version -ne $verified.Current.Version -or $plan.Rollback.SourceCommit -ne $verified.Current.SourceCommit -or
   $plan.Rollback.ArchiveSha256 -ne $verified.Current.ArchiveSha256 -or
   -not [bool]$plan.Compatibility.RollbackSafe -or -not [bool]$plan.Compatibility.CanarySafe){
    throw 'Rollout plan does not match the reverified immutable releases'
}
if([int]$plan.Compatibility.Database.CurrentTarget -ne [int]$verified.Database.CurrentTarget -or
   [int]$plan.Compatibility.Database.CandidateTarget -ne [int]$verified.Database.CandidateTarget -or
   [bool]$plan.Compatibility.Database.CurrentCanReadCandidate -ne [bool]$verified.Database.CurrentCanReadCandidate -or
   [bool]$plan.Compatibility.Database.CandidateCanReadCurrent -ne [bool]$verified.Database.CandidateCanReadCurrent -or
   [bool]$plan.Compatibility.Protocol.Identical -ne [bool]$verified.Protocol.Identical -or
   $plan.Compatibility.Protocol.CandidateRollingMode -ne $verified.Protocol.CandidateRollingMode -or
   $plan.Compatibility.Protocol.ReviewId -ne $verified.Protocol.ReviewId){
    throw 'Rollout compatibility details do not match the reverified releases'
}

[ordered]@{Passed=$true;ChangeId=$plan.ChangeId;CurrentVersion=$plan.Current.Version;
    CandidateVersion=$plan.Candidate.Version;PlanSha256=(Get-FileHash -LiteralPath $resolvedPlan -Algorithm SHA256).Hash.ToLowerInvariant();
    RequesterSignature=$true;ApproverSignature=$true;ProductionApproved=[bool]$plan.ProductionApproved;
    ExpiresAtUtc=$plan.ExpiresAtUtc;RollbackVersion=$plan.Rollback.Version}|ConvertTo-Json

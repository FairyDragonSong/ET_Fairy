param(
    [Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$RequesterPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedRequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedApproverKeyId,
    [Parameter(Mandatory)][string]$CurrentReleaseDirectory,
    [Parameter(Mandatory)][string]$CandidateReleaseDirectory,
    [Parameter(Mandatory)][string]$CurrentTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CurrentExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$CandidateTrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CandidateExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [Parameter(Mandatory)][string]$ObservationFile,
    [Parameter(Mandatory)][string]$ObservationSignatureFile,
    [Parameter(Mandatory)][string]$MonitoringPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedMonitoringKeyId,
    [Parameter(Mandatory)][ValidateRange(1,100)][int]$ExpectedWavePercent,
    [switch]$AllowTestPlan,
    [switch]$AllowDirtyTestArtifacts
)

$ErrorActionPreference='Stop'
$planParameters=@{
    PlanFile=$PlanFile;RequesterPublicKeyFile=$RequesterPublicKeyFile;ExpectedRequesterKeyId=$ExpectedRequesterKeyId
    ApproverPublicKeyFile=$ApproverPublicKeyFile;ExpectedApproverKeyId=$ExpectedApproverKeyId
    MonitoringPublicKeyFile=$MonitoringPublicKeyFile;ExpectedMonitoringKeyId=$ExpectedMonitoringKeyId
    CurrentReleaseDirectory=$CurrentReleaseDirectory;CandidateReleaseDirectory=$CandidateReleaseDirectory
    CurrentTrustedPublicKeyFile=$CurrentTrustedPublicKeyFile;CurrentExpectedSigningKeyId=$CurrentExpectedSigningKeyId
    CandidateTrustedPublicKeyFile=$CandidateTrustedPublicKeyFile;CandidateExpectedSigningKeyId=$CandidateExpectedSigningKeyId
    ExpectedPackageSupplier=$ExpectedPackageSupplier;ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase
}
if($AllowDirtyTestArtifacts){$planParameters.AllowDirtyTestArtifacts=$true}
if($AllowTestPlan){$planParameters.AllowTestPlan=$true}
& "$PSScriptRoot/Test-ServerRolloutPlan.ps1" @planParameters|Out-Null
$plan=Get-Content -LiteralPath ([IO.Path]::GetFullPath($PlanFile)) -Raw|ConvertFrom-Json -Depth 15 -DateKind String

$observationPath=[IO.Path]::GetFullPath($ObservationFile)
$signaturePath=[IO.Path]::GetFullPath($ObservationSignatureFile)
$monitoringKeyPath=[IO.Path]::GetFullPath($MonitoringPublicKeyFile)
foreach($path in @($observationPath,$signaturePath,$monitoringKeyPath)){
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Observation evidence or monitoring trust file is missing'}
}
$observationBytes=[IO.File]::ReadAllBytes($observationPath)
$monitoringPem=[IO.File]::ReadAllText($monitoringKeyPath)
if($monitoringPem -match 'PRIVATE KEY'){throw 'Monitoring verifier requires a public key'}
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem($monitoringPem)
    if($rsa.KeySize -lt 3072){throw 'Monitoring public key must be at least RSA-3072'}
    if(-not $rsa.VerifyData($observationBytes,[IO.File]::ReadAllBytes($signaturePath),
        [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){
        throw 'Monitoring observation signature is not trusted'
    }
}
finally{$rsa.Dispose()}
try{$observation=[Text.Encoding]::UTF8.GetString($observationBytes)|ConvertFrom-Json -Depth 10 -DateKind String}
catch{throw 'Monitoring observation is not valid JSON'}

$started=[DateTimeOffset]::MinValue
$ended=[DateTimeOffset]::MinValue
$planCreated=[DateTimeOffset]$plan.CreatedAtUtc
$planExpires=[DateTimeOffset]$plan.ExpiresAtUtc
if($observation.SchemaVersion -ne 1 -or $observation.Product -ne 'ET.Server' -or
   $observation.ChangeId -ne $plan.ChangeId -or $observation.CandidateVersion -ne $plan.Candidate.Version -or
   $observation.CandidateArchiveSha256 -ne $plan.Candidate.ArchiveSha256 -or
   $observation.MonitoringKeyId -ne $plan.MonitoringKeyId -or [int]$observation.WavePercent -ne $ExpectedWavePercent -or
   $ExpectedWavePercent -notin @($plan.Rollout.WavesPercent)){
    throw 'Monitoring observation identity does not match the rollout plan'
}
if(-not [DateTimeOffset]::TryParse([string]$observation.StartedAtUtc,[ref]$started) -or
   -not [DateTimeOffset]::TryParse([string]$observation.EndedAtUtc,[ref]$ended)){
    throw 'Monitoring observation timestamps are invalid'
}
$maximumObservationEnd=$(if($AllowTestPlan){[DateTimeOffset]::UtcNow.AddMinutes(5)}else{[DateTimeOffset]::UtcNow.AddSeconds(30)})
if($started -lt $planCreated -or $ended -le $started -or $ended -gt $maximumObservationEnd -or
   $ended -gt $planExpires){
    throw 'Monitoring observation time range is outside the signed rollout window'
}
$numericFields=@('Samples','ReadySamples','Requests','Errors','P95Milliseconds','CrashCount','JournalFailures','SaveFailures')
foreach($field in $numericFields){
    if($null -eq $observation.$field -or [decimal]$observation.$field -lt 0){throw "Monitoring observation field is invalid: $field"}
}
if([long]$observation.Samples -lt 1 -or [long]$observation.ReadySamples -gt [long]$observation.Samples -or
   [long]$observation.Requests -lt 1 -or [long]$observation.Errors -gt [long]$observation.Requests){
    throw 'Monitoring observation sample or request counts are invalid'
}
$durationMinutes=($ended-$started).TotalMinutes
$readyPercent=100*[decimal]$observation.ReadySamples/[decimal]$observation.Samples
$errorRatePercent=100*[decimal]$observation.Errors/[decimal]$observation.Requests
$reasons=[Collections.Generic.List[string]]::new()
if([long]$observation.CrashCount -gt 0){$reasons.Add('crash-count')}
if([long]$observation.JournalFailures -gt 0){$reasons.Add('session-journal-failure')}
if([long]$observation.SaveFailures -gt 0){$reasons.Add('role-save-failure')}
if($readyPercent -lt [decimal]$plan.Rollout.MinimumReadyPercent){$reasons.Add('readiness-ratio')}
if($errorRatePercent -gt [decimal]$plan.Rollout.MaximumErrorRatePercent){$reasons.Add('error-rate')}
if([decimal]$observation.P95Milliseconds -gt [decimal]$plan.Rollout.MaximumP95Milliseconds){$reasons.Add('p95-latency')}

$decision='PROMOTE';$exitCode=0
if($reasons.Count -gt 0){$decision='ROLLBACK';$exitCode=3}
elseif($durationMinutes -lt [int]$plan.Rollout.ObservationMinutes -or
       [long]$observation.Samples -lt [int]$plan.Rollout.ObservationMinutes){
    $decision='HOLD';$exitCode=2;$reasons.Add('observation-incomplete')
}
[ordered]@{
    Passed=($decision -eq 'PROMOTE')
    Decision=$decision
    ExitCode=$exitCode
    ChangeId=$plan.ChangeId
    CandidateVersion=$plan.Candidate.Version
    WavePercent=$ExpectedWavePercent
    DurationMinutes=[Math]::Round($durationMinutes,3)
    ReadyPercent=[Math]::Round($readyPercent,3)
    ErrorRatePercent=[Math]::Round($errorRatePercent,3)
    P95Milliseconds=[decimal]$observation.P95Milliseconds
    Reasons=$reasons
    RollbackVersion=$plan.Rollback.Version
    ObservationSha256=(Get-FileHash -LiteralPath $observationPath -Algorithm SHA256).Hash.ToLowerInvariant()
}|ConvertTo-Json -Depth 5
exit $exitCode

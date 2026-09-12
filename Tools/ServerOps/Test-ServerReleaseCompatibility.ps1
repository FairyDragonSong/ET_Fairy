param(
    [Parameter(Mandatory)][string]$CurrentReleaseDirectory,
    [Parameter(Mandatory)][string]$CandidateReleaseDirectory,
    [string]$CurrentTrustedPublicKeyFile,
    [ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CurrentExpectedSigningKeyId,
    [string]$CandidateTrustedPublicKeyFile,
    [ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$CandidateExpectedSigningKeyId,
    [ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [uri]$ExpectedSbomNamespaceBase,
    [switch]$AllowUnsignedTestArtifacts,
    [switch]$AllowDirtyTestArtifacts
)

$ErrorActionPreference='Stop'
if($AllowUnsignedTestArtifacts){
    if($CurrentTrustedPublicKeyFile -or $CurrentExpectedSigningKeyId -or $CandidateTrustedPublicKeyFile -or $CandidateExpectedSigningKeyId){
        throw 'Unsigned test mode cannot be combined with signing trust parameters'
    }
}
elseif(-not $CurrentTrustedPublicKeyFile -or -not $CurrentExpectedSigningKeyId -or
       -not $CandidateTrustedPublicKeyFile -or -not $CandidateExpectedSigningKeyId){
    throw 'Production compatibility verification requires trust files and key ids for both releases'
}
if(-not $AllowUnsignedTestArtifacts -and ([string]::IsNullOrWhiteSpace($ExpectedPackageSupplier) -or $null -eq $ExpectedSbomNamespaceBase)){
    throw 'Production compatibility verification requires trusted SBOM identity'
}

function Test-OneRelease([string]$Directory,[string]$PublicKey,[string]$KeyId){
    $parameters=@{ReleaseDirectory=$Directory}
    if($AllowUnsignedTestArtifacts){$parameters.AllowUnsignedTestArtifact=$true}
    else{
        $parameters.TrustedPublicKeyFile=$PublicKey
        $parameters.ExpectedSigningKeyId=$KeyId
        $parameters.ExpectedPackageSupplier=$ExpectedPackageSupplier
        $parameters.ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase
    }
    if(-not $AllowDirtyTestArtifacts){$parameters.RequireCleanProvenance=$true}
    $raw=& "$PSScriptRoot/Test-ServerRelease.ps1" @parameters
    try{return ($raw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12}
    catch{throw 'Release verifier did not return valid JSON'}
}

$current=Test-OneRelease $CurrentReleaseDirectory $CurrentTrustedPublicKeyFile $CurrentExpectedSigningKeyId
$candidate=Test-OneRelease $CandidateReleaseDirectory $CandidateTrustedPublicKeyFile $CandidateExpectedSigningKeyId
if($null -eq $current.Compatibility -or $null -eq $candidate.Compatibility){
    throw 'Both releases require a schema-2 compatibility contract'
}
if($current.Platform -ne $candidate.Platform){throw 'Candidate platform differs from current release'}
$currentVersion=[System.Management.Automation.SemanticVersion]$current.Version
$candidateVersion=[System.Management.Automation.SemanticVersion]$candidate.Version
if($candidateVersion.CompareTo($currentVersion) -le 0){throw 'Candidate version must be newer than current version'}

$currentContract=$current.Compatibility
$candidateContract=$candidate.Compatibility
$currentCanReadCandidate=[int]$candidateContract.DatabaseTarget -ge [int]$currentContract.DatabaseMinimumReadable -and
    [int]$candidateContract.DatabaseTarget -le [int]$currentContract.DatabaseMaximumReadable
$candidateCanReadCurrent=[int]$currentContract.DatabaseTarget -ge [int]$candidateContract.DatabaseMinimumReadable -and
    [int]$currentContract.DatabaseTarget -le [int]$candidateContract.DatabaseMaximumReadable
if(-not $currentCanReadCandidate){
    throw 'Rollback blocked: current release cannot read the candidate database target'
}
if(-not $candidateCanReadCurrent){
    throw 'Canary blocked: candidate release cannot read the current database target'
}
$protocolIdentical=$currentContract.ProtocolSha256 -eq $candidateContract.ProtocolSha256
$reviewedCompatibility=$candidateContract.RollingMode -eq 'reviewed-backward-compatible' -and
    $candidateContract.ReviewId -match '^[0-9A-Za-z._-]{3,80}$'
if(-not $protocolIdentical -and -not $reviewedCompatibility){
    throw 'Canary blocked: protocol changed without a reviewed backward-compatibility declaration'
}

[ordered]@{
    Passed=$true
    Current=[ordered]@{Version=$current.Version;SourceCommit=$current.SourceCommit;ArchiveSha256=$current.Sha256}
    Candidate=[ordered]@{Version=$candidate.Version;SourceCommit=$candidate.SourceCommit;ArchiveSha256=$candidate.Sha256}
    Platform=$candidate.Platform
    Database=[ordered]@{
        CurrentTarget=[int]$currentContract.DatabaseTarget
        CandidateTarget=[int]$candidateContract.DatabaseTarget
        CurrentCanReadCandidate=$currentCanReadCandidate
        CandidateCanReadCurrent=$candidateCanReadCurrent
    }
    Protocol=[ordered]@{
        Identical=$protocolIdentical
        CandidateRollingMode=$candidateContract.RollingMode
        ReviewId=$candidateContract.ReviewId
    }
    RollbackSafe=$currentCanReadCandidate
    CanarySafe=$candidateCanReadCurrent-and($protocolIdentical-or$reviewedCompatibility)
}|ConvertTo-Json -Depth 8

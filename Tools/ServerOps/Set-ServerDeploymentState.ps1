param(
    [Parameter(Mandatory)][ValidateSet('Bootstrap','StageCandidate','RequestSwitch','CommitSwitch','CommitRollback')][string]$Action,
    [Parameter(Mandatory)][string]$DeploymentRoot,
    [Parameter(Mandatory)][string]$ConfirmDeploymentRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$DeploymentId,
    [Parameter(Mandatory)][string]$SecretRoot,
    [Parameter(Mandatory)][string]$StatePrivateKeyFile,
    [Parameter(Mandatory)][string]$StatePublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$StateSignerKeyId,
    [Parameter(Mandatory)][string]$CurrentSlotDirectory,
    [Parameter(Mandatory)][string]$CurrentTrustedPublicKeyFile,
    [Parameter(Mandatory)][string]$CurrentExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [string]$CandidateSlotDirectory,[string]$CandidateTrustedPublicKeyFile,[string]$CandidateExpectedSigningKeyId,
    [string]$PlanFile,[string]$RequesterPublicKeyFile,[string]$ExpectedRequesterKeyId,
    [string]$ApproverPublicKeyFile,[string]$ExpectedApproverKeyId,
    [string]$MonitoringPublicKeyFile,[string]$ExpectedMonitoringKeyId,
    [string]$ObservationFile,[string]$ObservationSignatureFile,[ValidateRange(1,100)][int]$ExpectedWavePercent,
    [string]$ConfirmObservedSlotId,[ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$TransitionReason,
    [switch]$AllowTestPlan,[switch]$AllowDirtyTestArtifact
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/DeploymentState.Common.ps1"
$root=Assert-DeploymentPathOutsideRepository $DeploymentRoot 'DeploymentRoot'
if($ConfirmDeploymentRoot -cne $root){throw 'ConfirmDeploymentRoot must exactly match the resolved deployment root'}
$secret=Assert-DeploymentPathOutsideRepository $SecretRoot 'SecretRoot'
$private=Get-DeploymentFullPath $StatePrivateKeyFile
if(-not(($private+[IO.Path]::DirectorySeparatorChar).StartsWith(
    $secret+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase))){
    throw 'StatePrivateKeyFile must be beneath SecretRoot'
}
$stateFingerprint=Get-RsaPublicFingerprint $StatePublicKeyFile
if((Get-RsaPublicFingerprint $StatePrivateKeyFile) -cne $stateFingerprint){throw 'Deployment state private/public keys do not match'}
$slotCommon=@{ExpectedPackageSupplier=$ExpectedPackageSupplier;ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase}
if($AllowDirtyTestArtifact){$slotCommon.AllowDirtyTestArtifact=$true}
$currentRaw=& "$PSScriptRoot/Test-InstalledServerSlot.ps1" -SlotDirectory $CurrentSlotDirectory `
    -TrustedPublicKeyFile $CurrentTrustedPublicKeyFile -ExpectedSigningKeyId $CurrentExpectedSigningKeyId @slotCommon
$current=($currentRaw -join [Environment]::NewLine)|ConvertFrom-Json
if((Get-DeploymentFullPath $CurrentSlotDirectory) -cne (Get-DeploymentFullPath (Join-Path (Join-Path $root 'releases') $current.SlotId))){
    throw 'Current slot must be the verified immutable slot beneath DeploymentRoot/releases'
}
if((Get-RsaPublicFingerprint $CurrentTrustedPublicKeyFile) -ceq $stateFingerprint){throw 'State signer cannot reuse a release signing key'}

if($Action -eq 'Bootstrap'){
    if(Test-Path -LiteralPath (Join-Path $root 'state')){throw 'Deployment state already exists; bootstrap cannot overwrite it'}
    $state=[ordered]@{SchemaVersion=1;Product='ET.Server';DeploymentId=$DeploymentId;Generation=1;
        Phase='Stable';ActiveSlot=$current.SlotId;DesiredSlot=$current.SlotId;PreviousSlot=$null;CandidateSlot=$null;
        PlanFile=$null;PlanSha256=$null;DecisionObservationFile=$null;DecisionObservationSha256=$null;
        StateSignerKeyId=$StateSignerKeyId;StateSignerPublicKeySha256=$stateFingerprint;
        PreviousStateSha256=$null;UpdatedAtUtc=[DateTime]::UtcNow.ToString('O')}
    $hash=Save-DeploymentState $state $root $StatePrivateKeyFile
}else{
    foreach($required in @('CandidateSlotDirectory','CandidateTrustedPublicKeyFile','CandidateExpectedSigningKeyId','PlanFile',
        'RequesterPublicKeyFile','ExpectedRequesterKeyId','ApproverPublicKeyFile','ExpectedApproverKeyId',
        'MonitoringPublicKeyFile','ExpectedMonitoringKeyId')){
        if(-not(Get-Variable -Name $required -ValueOnly)){throw "$required is required for StageCandidate"}
    }
    $verified=Get-DeploymentState $root $StatePublicKeyFile $StateSignerKeyId;$old=$verified.State
    if($old.DeploymentId -cne $DeploymentId -or $old.ActiveSlot -cne $current.SlotId){
        throw 'Deployment state does not match the supplied current slot'
    }
    $candidateRaw=& "$PSScriptRoot/Test-InstalledServerSlot.ps1" -SlotDirectory $CandidateSlotDirectory `
        -TrustedPublicKeyFile $CandidateTrustedPublicKeyFile -ExpectedSigningKeyId $CandidateExpectedSigningKeyId @slotCommon
    $candidate=($candidateRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if((Get-DeploymentFullPath $CandidateSlotDirectory) -cne (Get-DeploymentFullPath (Join-Path (Join-Path $root 'releases') $candidate.SlotId))){
        throw 'Candidate slot must be the verified immutable slot beneath DeploymentRoot/releases'
    }
    foreach($publicKey in @($CandidateTrustedPublicKeyFile,$RequesterPublicKeyFile,$ApproverPublicKeyFile,$MonitoringPublicKeyFile)){
        if((Get-RsaPublicFingerprint $publicKey) -ceq $stateFingerprint){throw 'State signer must be independent from release, approval, and monitoring keys'}
    }
    $planParameters=@{PlanFile=$PlanFile;RequesterPublicKeyFile=$RequesterPublicKeyFile;ExpectedRequesterKeyId=$ExpectedRequesterKeyId;
        ApproverPublicKeyFile=$ApproverPublicKeyFile;ExpectedApproverKeyId=$ExpectedApproverKeyId;
        CurrentReleaseDirectory=(Join-Path $CurrentSlotDirectory 'evidence');CandidateReleaseDirectory=(Join-Path $CandidateSlotDirectory 'evidence');
        CurrentTrustedPublicKeyFile=$CurrentTrustedPublicKeyFile;CurrentExpectedSigningKeyId=$CurrentExpectedSigningKeyId;
        CandidateTrustedPublicKeyFile=$CandidateTrustedPublicKeyFile;CandidateExpectedSigningKeyId=$CandidateExpectedSigningKeyId;
        ExpectedPackageSupplier=$ExpectedPackageSupplier;ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase;
        MonitoringPublicKeyFile=$MonitoringPublicKeyFile;ExpectedMonitoringKeyId=$ExpectedMonitoringKeyId}
    if($AllowTestPlan){$planParameters.AllowTestPlan=$true};if($AllowDirtyTestArtifact){$planParameters.AllowDirtyTestArtifacts=$true}
    $planRaw=& "$PSScriptRoot/Test-ServerRolloutPlan.ps1" @planParameters
    $planResult=($planRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $planResult.Passed){throw 'Rollout plan verification failed'}
    $plan=Get-Content -LiteralPath $PlanFile -Raw|ConvertFrom-Json -Depth 15 -DateKind String
    if($plan.Current.ArchiveSha256 -cne $current.ArchiveSha256 -or $plan.Candidate.ArchiveSha256 -cne $candidate.ArchiveSha256){
        throw 'Rollout plan does not bind the supplied installed slots'
    }
    $planPath=Get-DeploymentFullPath $PlanFile
    $planHash=(Get-FileHash -LiteralPath $PlanFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $generation=[int64]$old.Generation+1
    if($Action -eq 'StageCandidate'){
        if($old.Phase -ne 'Stable'){throw 'Candidate can only be staged from Stable state'}
        $state=[ordered]@{SchemaVersion=1;Product='ET.Server';DeploymentId=$DeploymentId;Generation=$generation;
            Phase='CandidateStaged';ActiveSlot=$current.SlotId;DesiredSlot=$current.SlotId;PreviousSlot=$null;CandidateSlot=$candidate.SlotId;
            PlanFile=$planPath;PlanSha256=$planHash;DecisionObservationFile=$null;DecisionObservationSha256=$null;
            TransitionReason='candidate-staged';StateSignerKeyId=$StateSignerKeyId;StateSignerPublicKeySha256=$stateFingerprint;
            PreviousStateSha256=$verified.Sha256;UpdatedAtUtc=[DateTime]::UtcNow.ToString('O')}
    }elseif($Action -eq 'RequestSwitch'){
        if($old.Phase -ne 'CandidateStaged' -or $old.CandidateSlot -cne $candidate.SlotId -or
           $old.PlanSha256 -cne $planHash){throw 'Switch request does not match the staged candidate'}
        if(-not $ObservationFile -or -not $ObservationSignatureFile -or $ExpectedWavePercent -lt 1){
            throw 'Signed monitoring observation and wave are required for RequestSwitch'
        }
        $decisionArguments=@('-PlanFile',$PlanFile,'-RequesterPublicKeyFile',$RequesterPublicKeyFile,
            '-ExpectedRequesterKeyId',$ExpectedRequesterKeyId,'-ApproverPublicKeyFile',$ApproverPublicKeyFile,
            '-ExpectedApproverKeyId',$ExpectedApproverKeyId,'-CurrentReleaseDirectory',(Join-Path $CurrentSlotDirectory 'evidence'),
            '-CandidateReleaseDirectory',(Join-Path $CandidateSlotDirectory 'evidence'),'-CurrentTrustedPublicKeyFile',$CurrentTrustedPublicKeyFile,
            '-CurrentExpectedSigningKeyId',$CurrentExpectedSigningKeyId,'-CandidateTrustedPublicKeyFile',$CandidateTrustedPublicKeyFile,
            '-CandidateExpectedSigningKeyId',$CandidateExpectedSigningKeyId,'-ExpectedPackageSupplier',$ExpectedPackageSupplier,
            '-ExpectedSbomNamespaceBase',[string]$ExpectedSbomNamespaceBase,'-ObservationFile',$ObservationFile,
            '-ObservationSignatureFile',$ObservationSignatureFile,'-MonitoringPublicKeyFile',$MonitoringPublicKeyFile,
            '-ExpectedMonitoringKeyId',$ExpectedMonitoringKeyId,'-ExpectedWavePercent',[string]$ExpectedWavePercent)
        if($AllowTestPlan){$decisionArguments+='-AllowTestPlan'};if($AllowDirtyTestArtifact){$decisionArguments+='-AllowDirtyTestArtifacts'}
        $decisionRaw=& pwsh -NoProfile -File "$PSScriptRoot/Get-ServerRolloutDecision.ps1" @decisionArguments 2>&1
        if($LASTEXITCODE -ne 0){throw "Only a verified PROMOTE decision can request a switch: $($decisionRaw -join ' ')"}
        $decision=($decisionRaw -join [Environment]::NewLine)|ConvertFrom-Json
        if($decision.Decision -ne 'PROMOTE'){throw 'Monitoring decision was not PROMOTE'}
        $state=[ordered]@{SchemaVersion=1;Product='ET.Server';DeploymentId=$DeploymentId;Generation=$generation;
            Phase='SwitchPending';ActiveSlot=$current.SlotId;DesiredSlot=$candidate.SlotId;PreviousSlot=$current.SlotId;CandidateSlot=$candidate.SlotId;
            PlanFile=$planPath;PlanSha256=$planHash;DecisionObservationFile=(Get-DeploymentFullPath $ObservationFile);
            DecisionObservationSha256=$decision.ObservationSha256;TransitionReason='promote-approved';StateSignerKeyId=$StateSignerKeyId;
            StateSignerPublicKeySha256=$stateFingerprint;PreviousStateSha256=$verified.Sha256;UpdatedAtUtc=[DateTime]::UtcNow.ToString('O')}
    }elseif($Action -eq 'CommitSwitch'){
        if($old.Phase -ne 'SwitchPending' -or $old.CandidateSlot -cne $candidate.SlotId -or
           $ConfirmObservedSlotId -cne $candidate.SlotId){throw 'Observed ready slot does not match the pending candidate'}
        $state=[ordered]@{SchemaVersion=1;Product='ET.Server';DeploymentId=$DeploymentId;Generation=$generation;
            Phase='Stable';ActiveSlot=$candidate.SlotId;DesiredSlot=$candidate.SlotId;PreviousSlot=$current.SlotId;CandidateSlot=$null;
            PlanFile=$planPath;PlanSha256=$planHash;DecisionObservationFile=$old.DecisionObservationFile;
            DecisionObservationSha256=$old.DecisionObservationSha256;TransitionReason='candidate-ready';StateSignerKeyId=$StateSignerKeyId;
            StateSignerPublicKeySha256=$stateFingerprint;PreviousStateSha256=$verified.Sha256;UpdatedAtUtc=[DateTime]::UtcNow.ToString('O')}
    }else{
        if($old.Phase -ne 'SwitchPending' -or $old.PreviousSlot -cne $current.SlotId -or
           $ConfirmObservedSlotId -cne $current.SlotId -or -not $TransitionReason){
            throw 'Rollback confirmation does not match the pending previous slot'
        }
        $state=[ordered]@{SchemaVersion=1;Product='ET.Server';DeploymentId=$DeploymentId;Generation=$generation;
            Phase='Stable';ActiveSlot=$current.SlotId;DesiredSlot=$current.SlotId;PreviousSlot=$null;CandidateSlot=$null;
            PlanFile=$planPath;PlanSha256=$planHash;DecisionObservationFile=$old.DecisionObservationFile;
            DecisionObservationSha256=$old.DecisionObservationSha256;TransitionReason=$TransitionReason;StateSignerKeyId=$StateSignerKeyId;
            StateSignerPublicKeySha256=$stateFingerprint;PreviousStateSha256=$verified.Sha256;UpdatedAtUtc=[DateTime]::UtcNow.ToString('O')}
    }
    $hash=Save-DeploymentState $state $root $StatePrivateKeyFile
}
[ordered]@{Passed=$true;Action=$Action;DeploymentId=$DeploymentId;Generation=[int64]$state.Generation;
    Phase=$state.Phase;ActiveSlot=$state.ActiveSlot;CandidateSlot=$state.CandidateSlot;StateSha256=$hash}|ConvertTo-Json

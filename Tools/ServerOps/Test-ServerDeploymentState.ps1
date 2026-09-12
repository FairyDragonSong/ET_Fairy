param(
    [Parameter(Mandatory)][string]$DeploymentRoot,
    [Parameter(Mandatory)][string]$StatePublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedStateSignerKeyId
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/DeploymentState.Common.ps1"
$root=Assert-DeploymentPathOutsideRepository $DeploymentRoot 'DeploymentRoot'
$verified=Get-DeploymentState $root $StatePublicKeyFile $ExpectedStateSignerKeyId
$state=$verified.State
[ordered]@{Passed=$true;DeploymentId=$state.DeploymentId;Generation=[int64]$state.Generation;
    Phase=$state.Phase;ActiveSlot=$state.ActiveSlot;PreviousSlot=$state.PreviousSlot;
    CandidateSlot=$state.CandidateSlot;DesiredSlot=$state.DesiredSlot;StateSha256=$verified.Sha256}|ConvertTo-Json

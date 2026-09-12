param(
    [Parameter(Mandatory)][string]$DeploymentRoot,[Parameter(Mandatory)][string]$ConfirmDeploymentRoot,
    [Parameter(Mandatory)][string]$DeploymentId,[Parameter(Mandatory)][string]$SecretRoot,
    [Parameter(Mandatory)][string]$StatePrivateKeyFile,[Parameter(Mandatory)][string]$StatePublicKeyFile,
    [Parameter(Mandatory)][string]$StateSignerKeyId,[Parameter(Mandatory)][string]$CurrentSlotDirectory,
    [Parameter(Mandatory)][string]$CurrentTrustedPublicKeyFile,[Parameter(Mandatory)][string]$CurrentExpectedSigningKeyId,
    [Parameter(Mandatory)][string]$CandidateSlotDirectory,[Parameter(Mandatory)][string]$CandidateTrustedPublicKeyFile,
    [Parameter(Mandatory)][string]$CandidateExpectedSigningKeyId,[Parameter(Mandatory)][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,[Parameter(Mandatory)][string]$PlanFile,
    [Parameter(Mandatory)][string]$RequesterPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedRequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedApproverKeyId,
    [Parameter(Mandatory)][string]$MonitoringPublicKeyFile,[Parameter(Mandatory)][string]$ExpectedMonitoringKeyId,
    [Parameter(Mandatory)][string]$ObservationFile,[Parameter(Mandatory)][string]$ObservationSignatureFile,
    [Parameter(Mandatory)][ValidateRange(1,100)][int]$ExpectedWavePercent,
    [uri]$CurrentHealthUri='http://127.0.0.1:30300/health/ready',
    [uri]$CandidateHealthUri='http://127.0.0.1:30300/health/ready',
    [ValidateRange(1,300)][int]$StartupTimeoutSeconds=20,[ValidateRange(1,300)][int]$DrainTimeoutSeconds=15,
    [ValidateRange(1,300)][int]$CandidateStabilitySeconds=5,
    [string]$RuntimeRoot,
    [Parameter(Mandatory)][string]$ServerArguments,
    [string]$EmergencyStopFile,[string]$EmergencyStopTrustFile,
    [switch]$ExitAfterSuccessfulSwitch,[switch]$TestOnlySuppressCurrentShutdown,
    [switch]$AllowTestPlan,[switch]$AllowDirtyTestArtifact
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/DeploymentState.Common.ps1"
$resolvedDeploymentRoot=Assert-DeploymentPathOutsideRepository $DeploymentRoot 'DeploymentRoot'
if(-not $RuntimeRoot){$RuntimeRoot=Join-Path $resolvedDeploymentRoot 'runtime'}
$resolvedRuntimeRoot=Assert-DeploymentPathOutsideRepository $RuntimeRoot 'RuntimeRoot'
$releasePrefix=(Get-DeploymentFullPath (Join-Path $resolvedDeploymentRoot 'releases'))+[IO.Path]::DirectorySeparatorChar
if(($resolvedRuntimeRoot+[IO.Path]::DirectorySeparatorChar).StartsWith($releasePrefix,[StringComparison]::OrdinalIgnoreCase)){
    throw 'RuntimeRoot cannot be beneath immutable releases'
}
New-Item -ItemType Directory -Path $resolvedRuntimeRoot -Force|Out-Null
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
$readers=@{}
function Test-GlobalStop{
    return [bool]($EmergencyStopFile -and (Test-Path -LiteralPath ([IO.Path]::GetFullPath($EmergencyStopFile)) -PathType Leaf))
}
function Start-Slot([string]$SlotDirectory){
    if(Test-GlobalStop){throw 'Global emergency stop is present; refusing to start any slot'}
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $SlotDirectory 'package/Bin/App.exe'))
    $info.WorkingDirectory=Join-Path $SlotDirectory 'package/Bin';$info.Arguments=$ServerArguments
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['ET_LOG_PROFILE']='bounded'
    $info.Environment['ET_LOG_ROOT']=Join-Path (Join-Path $resolvedRuntimeRoot 'logs') $DeploymentId
    $info.Environment['ET_SECRET_ROOT']=$SecretRoot
    if($EmergencyStopFile){
        $info.Environment['ET_EMERGENCY_STOP_FILE']=[IO.Path]::GetFullPath($EmergencyStopFile)
        $info.Environment['ET_EMERGENCY_STOP_TRUST_FILE']=[IO.Path]::GetFullPath($EmergencyStopTrustFile)
        $info.Environment['ET_DEPLOYMENT_ID']=$DeploymentId
    }
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    if(-not $process.Start()){throw 'Server slot process failed to start'}
    $processes.Add($process);$readers[$process.Id]=@($process.StandardOutput.ReadToEndAsync(),$process.StandardError.ReadToEndAsync())
    return $process
}
function Wait-Ready([Diagnostics.Process]$Process,[uri]$Uri,[int]$Seconds){
    $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
    while([DateTime]::UtcNow-lt$deadline){
        if($Process.HasExited){return $false}
        if(Test-GlobalStop){return $false}
        try{if((Invoke-WebRequest -UseBasicParsing $Uri -TimeoutSec 1).StatusCode -eq 200){return $true}}catch{}
        Start-Sleep -Milliseconds 200
    }
    return $false
}
function Stop-Slot([Diagnostics.Process]$Process,[int]$Seconds){
    if($Process.HasExited){return $Process.ExitCode -eq 0}
    try{$Process.StandardInput.WriteLine('shutdown');$Process.StandardInput.Flush()}catch{return $false}
    return $Process.WaitForExit($Seconds*1000) -and $Process.ExitCode -eq 0
}
function Get-ProcessFailure([Diagnostics.Process]$Process){
    if(-not $Process.HasExited){return 'process-running'}
    $tasks=$readers[$Process.Id];[Threading.Tasks.Task]::WaitAll($tasks,2000)|Out-Null
    return "exit-$($Process.ExitCode): $($tasks[1].Result)"
}

$stopValues=@([bool]$EmergencyStopFile,[bool]$EmergencyStopTrustFile)
if($stopValues[0] -ne $stopValues[1]){throw 'Emergency stop file and trust file must be configured together'}
if($TestOnlySuppressCurrentShutdown -and -not $AllowTestPlan){throw 'Drain failure injection is only allowed with a test rollout plan'}
$stateParameters=@{DeploymentRoot=$DeploymentRoot;ConfirmDeploymentRoot=$ConfirmDeploymentRoot;DeploymentId=$DeploymentId;
    SecretRoot=$SecretRoot;StatePrivateKeyFile=$StatePrivateKeyFile;StatePublicKeyFile=$StatePublicKeyFile;
    StateSignerKeyId=$StateSignerKeyId;CurrentSlotDirectory=$CurrentSlotDirectory;
    CurrentTrustedPublicKeyFile=$CurrentTrustedPublicKeyFile;CurrentExpectedSigningKeyId=$CurrentExpectedSigningKeyId;
    CandidateSlotDirectory=$CandidateSlotDirectory;CandidateTrustedPublicKeyFile=$CandidateTrustedPublicKeyFile;
    CandidateExpectedSigningKeyId=$CandidateExpectedSigningKeyId;ExpectedPackageSupplier=$ExpectedPackageSupplier;
    ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase;PlanFile=$PlanFile;RequesterPublicKeyFile=$RequesterPublicKeyFile;
    ExpectedRequesterKeyId=$ExpectedRequesterKeyId;ApproverPublicKeyFile=$ApproverPublicKeyFile;
    ExpectedApproverKeyId=$ExpectedApproverKeyId;MonitoringPublicKeyFile=$MonitoringPublicKeyFile;
    ExpectedMonitoringKeyId=$ExpectedMonitoringKeyId}
if($AllowTestPlan){$stateParameters.AllowTestPlan=$true};if($AllowDirtyTestArtifact){$stateParameters.AllowDirtyTestArtifact=$true}
try{
    if(Test-GlobalStop){
        [ordered]@{Passed=$false;Outcome='EmergencyStopped';CandidateStarted=$false;CurrentStarted=$false}|ConvertTo-Json
        exit 4
    }
    $preflightRaw=& "$PSScriptRoot/Test-ServerDeploymentState.ps1" -DeploymentRoot $DeploymentRoot `
        -StatePublicKeyFile $StatePublicKeyFile -ExpectedStateSignerKeyId $StateSignerKeyId
    $preflight=($preflightRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $currentSlotId=[IO.Path]::GetFileName([IO.Path]::GetFullPath($CurrentSlotDirectory))
    $candidateSlotId=[IO.Path]::GetFileName([IO.Path]::GetFullPath($CandidateSlotDirectory))
    if($preflight.Phase -ne 'CandidateStaged' -or $preflight.ActiveSlot -cne $currentSlotId -or
       $preflight.CandidateSlot -cne $candidateSlotId){throw 'Signed staged state does not match supervisor slots'}
    $verifyCommon=@{ExpectedPackageSupplier=$ExpectedPackageSupplier;ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase}
    if($AllowDirtyTestArtifact){$verifyCommon.AllowDirtyTestArtifact=$true}
    & "$PSScriptRoot/Test-InstalledServerSlot.ps1" -SlotDirectory $CurrentSlotDirectory `
        -TrustedPublicKeyFile $CurrentTrustedPublicKeyFile -ExpectedSigningKeyId $CurrentExpectedSigningKeyId @verifyCommon|Out-Null
    & "$PSScriptRoot/Test-InstalledServerSlot.ps1" -SlotDirectory $CandidateSlotDirectory `
        -TrustedPublicKeyFile $CandidateTrustedPublicKeyFile -ExpectedSigningKeyId $CandidateExpectedSigningKeyId @verifyCommon|Out-Null
    $current=Start-Slot $CurrentSlotDirectory
    if(-not(Wait-Ready $current $CurrentHealthUri $StartupTimeoutSeconds)){throw 'Current slot failed readiness before switch'}
    & "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action RequestSwitch @stateParameters `
        -ObservationFile $ObservationFile -ObservationSignatureFile $ObservationSignatureFile `
        -ExpectedWavePercent $ExpectedWavePercent|Out-Null
    if(Test-GlobalStop){
        if(-not $current.HasExited){Stop-Slot $current $DrainTimeoutSeconds|Out-Null}
        [ordered]@{Passed=$false;Outcome='EmergencyStopped';CandidateStarted=$false}|ConvertTo-Json;exit 4
    }
    $drained=$(if($TestOnlySuppressCurrentShutdown){Start-Sleep -Seconds $DrainTimeoutSeconds;$false}else{Stop-Slot $current $DrainTimeoutSeconds})
    if(-not $drained){
        $detail=Get-ProcessFailure $current
        if(-not $current.HasExited){$current.Kill($true);$current.WaitForExit()}
        if(Test-GlobalStop){[ordered]@{Passed=$false;Outcome='EmergencyStopped';CandidateStarted=$false}|ConvertTo-Json;exit 4}
        $restored=Start-Slot $CurrentSlotDirectory
        if(-not(Wait-Ready $restored $CurrentHealthUri $StartupTimeoutSeconds)){throw 'Current drain failed and previous slot could not be restored'}
        & "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action CommitRollback @stateParameters `
            -ConfirmObservedSlotId ([IO.Path]::GetFileName([IO.Path]::GetFullPath($CurrentSlotDirectory))) `
            -TransitionReason 'current-drain-failed'|Out-Null
        if($ExitAfterSuccessfulSwitch){Stop-Slot $restored $DrainTimeoutSeconds|Out-Null}
        [ordered]@{Passed=$false;Outcome='CurrentDrainFailed';CandidateStarted=$false;PreviousRestored=$true;Detail=$detail}|ConvertTo-Json
        exit 2
    }
    if(Test-GlobalStop){[ordered]@{Passed=$false;Outcome='EmergencyStopped';CandidateStarted=$false}|ConvertTo-Json;exit 4}
    $candidate=Start-Slot $CandidateSlotDirectory
    $ready=Wait-Ready $candidate $CandidateHealthUri $StartupTimeoutSeconds
    if($ready){
        $stableUntil=[DateTime]::UtcNow.AddSeconds($CandidateStabilitySeconds)
        while([DateTime]::UtcNow-lt$stableUntil -and -not $candidate.HasExited -and -not(Test-GlobalStop)){Start-Sleep -Milliseconds 200}
        $ready=(-not $candidate.HasExited)-and(-not(Test-GlobalStop))
    }
    if(-not $ready){
        if(-not $candidate.HasExited){$candidate.Kill($true);$candidate.WaitForExit()}
        if(Test-GlobalStop){[ordered]@{Passed=$false;Outcome='EmergencyStopped';CandidateStarted=$true}|ConvertTo-Json;exit 4}
        $previous=Start-Slot $CurrentSlotDirectory
        if(-not(Wait-Ready $previous $CurrentHealthUri $StartupTimeoutSeconds)){throw 'Candidate failed and previous slot could not be restored'}
        & "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action CommitRollback @stateParameters `
            -ConfirmObservedSlotId ([IO.Path]::GetFileName([IO.Path]::GetFullPath($CurrentSlotDirectory))) `
            -TransitionReason 'candidate-readiness-failed'|Out-Null
        if($ExitAfterSuccessfulSwitch){Stop-Slot $previous $DrainTimeoutSeconds|Out-Null}
        [ordered]@{Passed=$false;Outcome='RolledBack';CandidateStarted=$true;PreviousRestored=$true}|ConvertTo-Json;exit 3
    }
    & "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action CommitSwitch @stateParameters `
        -ConfirmObservedSlotId ([IO.Path]::GetFileName([IO.Path]::GetFullPath($CandidateSlotDirectory)))|Out-Null
    if($ExitAfterSuccessfulSwitch){
        if(-not(Stop-Slot $candidate $DrainTimeoutSeconds)){throw 'Candidate verification shutdown failed'}
        [ordered]@{Passed=$true;Outcome='Switched';CandidateReady=$true;VerificationProcessStopped=$true}|ConvertTo-Json;exit 0
    }
    $candidate.WaitForExit()
    [ordered]@{Passed=($candidate.ExitCode-eq4);Outcome=$(if($candidate.ExitCode-eq4){'EmergencyStopped'}else{'CandidateExited'});
        ExitCode=$candidate.ExitCode}|ConvertTo-Json
    exit $(if($candidate.ExitCode-eq4){4}else{2})
}
finally{
    foreach($process in $processes){
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
}

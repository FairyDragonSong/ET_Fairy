param([switch]$AllowDirtyTestArtifacts)

$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-rollout-test-'+[guid]::NewGuid().ToString('N'))
$utf8=[Text.UTF8Encoding]::new($false)
New-Item -ItemType Directory -Path $fixture|Out-Null
try{
    $keys=@{}
    foreach($name in @('release-current','release-candidate','requester','approver','monitor','deployment-state')){
        $rsa=[Security.Cryptography.RSA]::Create(3072)
        try{
            $private=Join-Path $fixture "$name.private.pem"
            $public=Join-Path $fixture "$name.public.pem"
            [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8)
            [IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8)
            $keys[$name]=@{Private=$private;Public=$public}
        }
        finally{$rsa.Dispose()}
    }
    $releaseRoot=Join-Path $fixture 'releases'
    $currentVersion='0.0.1-rollout-test'
    $candidateVersion='0.0.2-rollout-test'
    $currentParameters=@{Version=$currentVersion;OutputRoot=$releaseRoot;
        SigningPrivateKeyFile=$keys['release-current'].Private;SigningKeyId='release-current';
        PackageSupplier='ET Local Test';SbomNamespaceBase='https://example.invalid/et-rollout'}
    $candidateParameters=@{Version=$candidateVersion;OutputRoot=$releaseRoot;
        SigningPrivateKeyFile=$keys['release-candidate'].Private;SigningKeyId='release-candidate';
        PackageSupplier='ET Local Test';SbomNamespaceBase='https://example.invalid/et-rollout'}
    if($AllowDirtyTestArtifacts){$currentParameters.AllowDirtyTestArtifact=$true;$candidateParameters.AllowDirtyTestArtifact=$true}
    & "$PSScriptRoot/New-ServerRelease.ps1" @currentParameters|Out-Null
    & "$PSScriptRoot/New-ServerRelease.ps1" @candidateParameters|Out-Null
    $current=Join-Path $releaseRoot $currentVersion
    $candidate=Join-Path $releaseRoot $candidateVersion
    $compatibilityParameters=@{
        CurrentReleaseDirectory=$current;CandidateReleaseDirectory=$candidate
        CurrentTrustedPublicKeyFile=$keys['release-current'].Public;CurrentExpectedSigningKeyId='release-current'
        CandidateTrustedPublicKeyFile=$keys['release-candidate'].Public;CandidateExpectedSigningKeyId='release-candidate'
        ExpectedPackageSupplier='ET Local Test';ExpectedSbomNamespaceBase='https://example.invalid/et-rollout'
    }
    if($AllowDirtyTestArtifacts){$compatibilityParameters.AllowDirtyTestArtifacts=$true}
    $compatibilityRaw=& "$PSScriptRoot/Test-ServerReleaseCompatibility.ps1" @compatibilityParameters
    $compatibility=($compatibilityRaw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12
    if(-not $compatibility.Passed -or -not $compatibility.RollbackSafe -or -not $compatibility.CanarySafe){
        throw 'Compatible signed releases were not approved'
    }

    $planParameters=$compatibilityParameters.Clone()
    $planParameters.ChangeId='rollout-automated-test';$planParameters.RequestedBy='test-requester';$planParameters.ApprovedBy='test-approver'
    $planParameters.RequesterPrivateKeyFile=$keys.requester.Private;$planParameters.RequesterKeyId='requester-test'
    $planParameters.ApproverPrivateKeyFile=$keys.approver.Private;$planParameters.ApproverKeyId='approver-test'
    $planParameters.MonitoringPublicKeyFile=$keys.monitor.Public;$planParameters.MonitoringKeyId='monitor-test'
    $planParameters.ConfirmCandidateVersion=$candidateVersion;$planParameters.OutputDirectory=(Join-Path $fixture 'plans')
    $planParameters.CanaryPercent=5;$planParameters.ObservationMinutes=5;$planParameters.ValidForMinutes=30
    $planParameters.TestOnlyPlan=$true
    & "$PSScriptRoot/New-ServerRolloutPlan.ps1" @planParameters|Out-Null
    $planFile=Join-Path $fixture 'plans/rollout-automated-test.rollout.json'
    $verifyParameters=$compatibilityParameters.Clone();$verifyParameters.PlanFile=$planFile
    $verifyParameters.RequesterPublicKeyFile=$keys.requester.Public;$verifyParameters.ExpectedRequesterKeyId='requester-test'
    $verifyParameters.ApproverPublicKeyFile=$keys.approver.Public;$verifyParameters.ExpectedApproverKeyId='approver-test'
    $verifyParameters.MonitoringPublicKeyFile=$keys.monitor.Public;$verifyParameters.ExpectedMonitoringKeyId='monitor-test'
    $verifyParameters.AllowTestPlan=$true
    $verifiedRaw=& "$PSScriptRoot/Test-ServerRolloutPlan.ps1" @verifyParameters
    $verified=($verifiedRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $verified.Passed -or $verified.ProductionApproved){throw 'Test rollout plan verification result is invalid'}

    $plan=Get-Content -LiteralPath $planFile -Raw|ConvertFrom-Json -Depth 15 -DateKind String
    function New-Observation([string]$Name,[int]$Minutes,[int]$Samples,[int]$Crashes){
        $started=[DateTimeOffset]$plan.CreatedAtUtc
        $observation=[ordered]@{SchemaVersion=1;Product='ET.Server';ChangeId=$plan.ChangeId;
            CandidateVersion=$plan.Candidate.Version;CandidateArchiveSha256=$plan.Candidate.ArchiveSha256;
            MonitoringKeyId='monitor-test';WavePercent=5;StartedAtUtc=$started.ToString('O');
            EndedAtUtc=$started.AddMinutes($Minutes).ToString('O');Samples=$Samples;ReadySamples=$Samples;
            Requests=100;Errors=0;P95Milliseconds=100;CrashCount=$Crashes;JournalFailures=0;SaveFailures=0}
        $path=Join-Path $fixture "$Name.observation.json"
        $bytes=$utf8.GetBytes(($observation|ConvertTo-Json -Depth 5));[IO.File]::WriteAllBytes($path,$bytes)
        $rsa=[Security.Cryptography.RSA]::Create()
        try{
            $rsa.ImportFromPem([IO.File]::ReadAllText($keys.monitor.Private))
            $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pss)
        }
        finally{$rsa.Dispose()}
        [IO.File]::WriteAllBytes("$path.sig",$signature)
        return $path
    }
    $decisionBase=@('-PlanFile',$planFile,'-RequesterPublicKeyFile',$keys.requester.Public,
        '-ExpectedRequesterKeyId','requester-test','-ApproverPublicKeyFile',$keys.approver.Public,
        '-ExpectedApproverKeyId','approver-test','-CurrentReleaseDirectory',$current,
        '-CandidateReleaseDirectory',$candidate,'-CurrentTrustedPublicKeyFile',$keys['release-current'].Public,
        '-CurrentExpectedSigningKeyId','release-current','-CandidateTrustedPublicKeyFile',$keys['release-candidate'].Public,
        '-CandidateExpectedSigningKeyId','release-candidate','-ExpectedPackageSupplier','ET Local Test',
        '-ExpectedSbomNamespaceBase','https://example.invalid/et-rollout','-MonitoringPublicKeyFile',$keys.monitor.Public,
        '-ExpectedMonitoringKeyId','monitor-test','-ExpectedWavePercent','5','-AllowTestPlan')
    if($AllowDirtyTestArtifacts){$decisionBase+='-AllowDirtyTestArtifacts'}
    $decisionScript=Join-Path $PSScriptRoot 'Get-ServerRolloutDecision.ps1'
    foreach($case in @(@{Name='promote';Minutes=5;Samples=5;Crashes=0;Expected=0;Decision='PROMOTE'},
        @{Name='hold';Minutes=1;Samples=1;Crashes=0;Expected=2;Decision='HOLD'},
        @{Name='rollback';Minutes=5;Samples=5;Crashes=1;Expected=3;Decision='ROLLBACK'})){
        $observation=New-Observation $case.Name $case.Minutes $case.Samples $case.Crashes
        $raw=& pwsh -NoProfile -File $decisionScript @decisionBase -ObservationFile $observation `
            -ObservationSignatureFile "$observation.sig" 2>&1
        $actualExit=$LASTEXITCODE
        if($actualExit -ne $case.Expected){throw "Rollout decision exit mismatch: $($case.Name)=$actualExit"}
        $decision=($raw -join [Environment]::NewLine)|ConvertFrom-Json
        if($decision.Decision -ne $case.Decision){throw "Rollout decision mismatch: $($case.Name)"}
    }

    $reverse=$compatibilityParameters.Clone()
    $reverse.CurrentReleaseDirectory=$candidate;$reverse.CandidateReleaseDirectory=$current
    $reverse.CurrentTrustedPublicKeyFile=$keys['release-candidate'].Public;$reverse.CurrentExpectedSigningKeyId='release-candidate'
    $reverse.CandidateTrustedPublicKeyFile=$keys['release-current'].Public;$reverse.CandidateExpectedSigningKeyId='release-current'
    $rejected=$false
    try{& "$PSScriptRoot/Test-ServerReleaseCompatibility.ps1" @reverse|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Reversed release versions were accepted'}

    $tamperedPlan=Join-Path $fixture 'plans/tampered.rollout.json'
    Copy-Item -LiteralPath $planFile -Destination $tamperedPlan
    Copy-Item -LiteralPath "$planFile.requester.sig" -Destination "$tamperedPlan.requester.sig"
    Copy-Item -LiteralPath "$planFile.approver.sig" -Destination "$tamperedPlan.approver.sig"
    [IO.File]::AppendAllText($tamperedPlan,' ')
    $tamperedVerify=$verifyParameters.Clone();$tamperedVerify.PlanFile=$tamperedPlan
    $rejected=$false
    try{& "$PSScriptRoot/Test-ServerRolloutPlan.ps1" @tamperedVerify|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Tampered rollout plan was accepted'}

    $duplicateApprovalKey=Join-Path $fixture 'duplicate-approver.private.pem'
    Copy-Item -LiteralPath $keys.requester.Private -Destination $duplicateApprovalKey
    $separationPlan=$planParameters.Clone();$separationPlan.ChangeId='rollout-duplicate-key-test'
    $separationPlan.ApproverPrivateKeyFile=$duplicateApprovalKey;$separationPlan.ApproverKeyId='another-key-id'
    $rejected=$false
    try{& "$PSScriptRoot/New-ServerRolloutPlan.ps1" @separationPlan|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Copied requester key was accepted as a distinct approver key'}
    $monitorReusePlan=$planParameters.Clone();$monitorReusePlan.ChangeId='rollout-monitor-key-test'
    $monitorReusePlan.MonitoringPublicKeyFile=$keys.requester.Public;$monitorReusePlan.MonitoringKeyId='monitor-reused'
    $rejected=$false
    try{& "$PSScriptRoot/New-ServerRolloutPlan.ps1" @monitorReusePlan|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Requester key was accepted as the monitoring identity'}

    $deploymentRoot=Join-Path $fixture 'deployment'
    $currentInstallParameters=@{ReleaseDirectory=$current;DeploymentRoot=$deploymentRoot;
        TrustedPublicKeyFile=$keys['release-current'].Public;ExpectedSigningKeyId='release-current';
        ExpectedPackageSupplier='ET Local Test';ExpectedSbomNamespaceBase='https://example.invalid/et-rollout';
        ConfirmDeploymentRoot=[IO.Path]::GetFullPath($deploymentRoot)}
    $installParameters=@{ReleaseDirectory=$candidate;DeploymentRoot=$deploymentRoot;
        TrustedPublicKeyFile=$keys['release-candidate'].Public;ExpectedSigningKeyId='release-candidate';
        ExpectedPackageSupplier='ET Local Test';ExpectedSbomNamespaceBase='https://example.invalid/et-rollout';
        ConfirmDeploymentRoot=[IO.Path]::GetFullPath($deploymentRoot)}
    if($AllowDirtyTestArtifacts){$currentInstallParameters.AllowDirtyTestArtifact=$true;$installParameters.AllowDirtyTestArtifact=$true}
    $currentInstalledRaw=& "$PSScriptRoot/Install-ServerReleaseSlot.ps1" @currentInstallParameters
    $currentInstalled=($currentInstalledRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $installedRaw=& "$PSScriptRoot/Install-ServerReleaseSlot.ps1" @installParameters
    $installed=($installedRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $slotParameters=@{SlotDirectory=$installed.SlotDirectory;TrustedPublicKeyFile=$keys['release-candidate'].Public;
        ExpectedSigningKeyId='release-candidate';ExpectedPackageSupplier='ET Local Test';
        ExpectedSbomNamespaceBase='https://example.invalid/et-rollout'}
    if($AllowDirtyTestArtifacts){$slotParameters.AllowDirtyTestArtifact=$true}
    $slotRaw=& "$PSScriptRoot/Test-InstalledServerSlot.ps1" @slotParameters
    $slot=($slotRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $slot.Passed -or $slot.ArchiveSha256 -ne $compatibility.Candidate.ArchiveSha256){
        throw 'Installed immutable slot did not match the candidate release'
    }
    $rejected=$false
    try{& "$PSScriptRoot/Install-ServerReleaseSlot.ps1" @installParameters|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Existing immutable slot was overwritten'}

    $stateCommon=@{DeploymentRoot=$deploymentRoot;ConfirmDeploymentRoot=[IO.Path]::GetFullPath($deploymentRoot);
        DeploymentId='rollout-state-test';SecretRoot=$fixture;StatePrivateKeyFile=$keys['deployment-state'].Private;
        StatePublicKeyFile=$keys['deployment-state'].Public;StateSignerKeyId='deployment-state-test';
        CurrentSlotDirectory=$currentInstalled.SlotDirectory;CurrentTrustedPublicKeyFile=$keys['release-current'].Public;
        CurrentExpectedSigningKeyId='release-current';ExpectedPackageSupplier='ET Local Test';
        ExpectedSbomNamespaceBase='https://example.invalid/et-rollout'}
    if($AllowDirtyTestArtifacts){$stateCommon.AllowDirtyTestArtifact=$true}
    $bootstrapRaw=& "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action Bootstrap @stateCommon
    $bootstrap=($bootstrapRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if($bootstrap.Generation -ne 1 -or $bootstrap.Phase -ne 'Stable'){throw 'Deployment state bootstrap failed'}
    $stageParameters=$stateCommon.Clone();$stageParameters.CandidateSlotDirectory=$installed.SlotDirectory
    $stageParameters.CandidateTrustedPublicKeyFile=$keys['release-candidate'].Public
    $stageParameters.CandidateExpectedSigningKeyId='release-candidate';$stageParameters.PlanFile=$planFile
    $stageParameters.RequesterPublicKeyFile=$keys.requester.Public;$stageParameters.ExpectedRequesterKeyId='requester-test'
    $stageParameters.ApproverPublicKeyFile=$keys.approver.Public;$stageParameters.ExpectedApproverKeyId='approver-test'
    $stageParameters.MonitoringPublicKeyFile=$keys.monitor.Public;$stageParameters.ExpectedMonitoringKeyId='monitor-test'
    $stageParameters.AllowTestPlan=$true
    $stageRaw=& "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action StageCandidate @stageParameters
    $staged=($stageRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if($staged.Generation -ne 2 -or $staged.Phase -ne 'CandidateStaged' -or $staged.CandidateSlot -ne $installed.SlotId){
        throw 'Signed candidate staging state is invalid'
    }
    $stateVerify=@{DeploymentRoot=$deploymentRoot;StatePublicKeyFile=$keys['deployment-state'].Public;
        ExpectedStateSignerKeyId='deployment-state-test'}
    $stateRaw=& "$PSScriptRoot/Test-ServerDeploymentState.ps1" @stateVerify
    $stateResult=($stateRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $stateResult.Passed -or $stateResult.Generation -ne 2){throw 'Deployment state chain verification failed'}
    $currentState=Join-Path $deploymentRoot 'state/current.json';$currentStateSignature=$currentState+'.sig'
    $savedState=[IO.File]::ReadAllBytes($currentState);$savedStateSignature=[IO.File]::ReadAllBytes($currentStateSignature)
    [IO.File]::AppendAllText($currentState,' ')
    $rejected=$false
    try{& "$PSScriptRoot/Test-ServerDeploymentState.ps1" @stateVerify|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Tampered deployment state was accepted'}
    [IO.File]::WriteAllBytes($currentState,$savedState);[IO.File]::WriteAllBytes($currentStateSignature,$savedStateSignature)
    $firstAudit=Get-ChildItem -LiteralPath (Join-Path $deploymentRoot 'state/audit') -File -Filter '00000001-*.json'
    $heldAudit=$firstAudit.FullName+'.held';Move-Item -LiteralPath $firstAudit.FullName -Destination $heldAudit
    $rejected=$false
    try{& "$PSScriptRoot/Test-ServerDeploymentState.ps1" @stateVerify|Out-Null}catch{$rejected=$true}
    Move-Item -LiteralPath $heldAudit -Destination $firstAudit.FullName
    if(-not $rejected){throw 'Incomplete deployment state audit chain was accepted'}
    $rejected=$false
    try{& "$PSScriptRoot/Set-ServerDeploymentState.ps1" -Action Bootstrap @stateCommon|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Existing deployment state was overwritten by bootstrap'}

    $hotfix=Join-Path $installed.SlotDirectory 'package/Bin/Hotfix.dll'
    $stream=[IO.File]::Open($hotfix,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{$stream.Position=0;$value=$stream.ReadByte();$stream.Position=0;$stream.WriteByte($value-bxor1)}finally{$stream.Dispose()}
    $rejected=$false
    try{& "$PSScriptRoot/Test-InstalledServerSlot.ps1" @slotParameters|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Tampered installed slot was accepted'}

    [ordered]@{Passed=$true;CurrentVersion=$currentVersion;CandidateVersion=$candidateVersion;
        ProtocolIdentical=[bool]$compatibility.Protocol.Identical;DatabaseRollbackSafe=[bool]$compatibility.RollbackSafe;
        DualApprovalVerified=$true;Decisions=@('PROMOTE:0','HOLD:2','ROLLBACK:3');
        TamperedPlanRejected=$true;ReversedVersionRejected=$true;CopiedApprovalKeyRejected=$true;
        MonitoringKeyReuseRejected=$true;ImmutableSlotVerified=$true;DuplicateSlotRejected=$true;
        TamperedSlotRejected=$true;SignedDeploymentStateVerified=$true;DeploymentStateGeneration=2;
        TamperedDeploymentStateRejected=$true;MissingDeploymentAuditRejected=$true;
        DeploymentStateRebootstrapRejected=$true}|ConvertTo-Json -Depth 5
}
finally{
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolvedFixture=[IO.Path]::GetFullPath($fixture)
    if($resolvedFixture.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and
       [IO.Path]::GetFileName($resolvedFixture) -match '^et-rollout-test-[a-f0-9]{32}$' -and
       (Test-Path -LiteralPath $resolvedFixture)){
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

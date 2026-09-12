param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[switch]$Resume,[switch]$Outage,[switch]$SaveFailure,[switch]$Baseline,[switch]$Governance,[switch]$LeaseExpiry,[switch]$Credentials,[switch]$SelfCredentials,
    [ValidateRange(1,3000)][int]$Clients=20,[ValidateRange(0,86400)][int]$DurationSeconds=0,[switch]$CapacityProfile,[switch]$BoundedLogs,[switch]$CrashJournal)
$ErrorActionPreference='Stop'
if(($Clients -ne 20 -or $DurationSeconds -ne 0) -and -not $Baseline){throw 'Load parameters require -Baseline'}
if($CapacityProfile -and -not $Baseline){throw 'Capacity profile requires isolated Baseline mode'}
if($CrashJournal -and (-not $Baseline -or $Clients -gt 100 -or $DurationSeconds -lt 60)){throw 'Crash journal requires Baseline, at most 100 clients and at least 60 sustained seconds'}
if(@($Resume,$Outage,$SaveFailure,$Baseline,$Governance,$LeaseExpiry,$Credentials,$SelfCredentials).Where({$_}).Count -gt 1){throw 'Select only one test mode'}
if($SaveFailure -or $LeaseExpiry){$Outage=$true}
. "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$env:ET_MONGO_DATABASE='ET_LOCAL_GAMEPLAY'
$env:ET_GAMEPLAY_LOOPBACK='1'
if(-not $Resume){$env:ET_GAMEPLAY_ACCOUNT='e2e_'+[Guid]::NewGuid().ToString('N').Substring(0,16);$env:ET_GAMEPLAY_PASSWORD=[Guid]::NewGuid().ToString('N')}
if($Outage){foreach($name in 'outage-stopped','outage-restored'){Remove-Item -LiteralPath "$env:ET_LOCAL_MONGO_ROOT/$name" -ErrorAction SilentlyContinue}}
$udpPorts=@((Get-NetUDPEndpoint).LocalPort)
$tcpPorts=@((Get-NetTCPConnection -State Listen).LocalPort)
foreach($port in @(20001,30002,30003,30004,30300,30301,30302,30303,30304)) {
    if($port -in $udpPorts -or $port -in $tcpPorts){throw "Port already occupied: $port"}
}
$info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
$info.WorkingDirectory="$repo/Bin"
$info.Arguments='--Process=1 --StartConfig=StartConfig/Localhost --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1 --Console=1 --ShutdownTimeoutSeconds=20'
if($CapacityProfile){$info.Arguments+=" --LoginIpLimit=$Clients"}
$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.Environment['ET_GAMEPLAY_CLIENTS']=[string]$Clients
$info.Environment['ET_GAMEPLAY_DURATION']=[string]$DurationSeconds
$info.Environment['ET_GAMEPLAY_CAPACITY_PROFILE']=$(if($CapacityProfile){'1'}else{'0'})
if($BoundedLogs){$info.Environment['ET_LOG_PROFILE']='bounded'}
foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
$process=[Diagnostics.Process]::new();$process.StartInfo=$info
$lines=[Collections.Generic.List[string]]::new()
$selfRoleCreated=$false
$processStarted=$false
$shutdownObserved=$false;$shutdownExitCode=$null;$shutdownElapsedMs=$null
$journalVerification=$null
$crashExitObserved=$false;$crashExit=$null
try {
    if($SelfCredentials){
        $env:ET_SELF_TEST_USER='et_self_test_'+[Guid]::NewGuid().ToString('N')
        $env:ET_SELF_TEST_PASSWORD=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
        $env:ET_SELF_TEST_CLEANUP='0'
        $selfRoleCreated=$true
        & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalSelfPasswordRole.js"
        if($LASTEXITCODE -ne 0){throw 'Self-password test role provisioning failed'}
        $info.Environment['ET_SELF_TEST_MONGO_URI']="mongodb://$($env:ET_SELF_TEST_USER):$($env:ET_SELF_TEST_PASSWORD)@localhost:27101,localhost:27102,localhost:27103/ET_LOCAL_GAMEPLAY?authSource=ET_LOCAL_GAMEPLAY&replicaSet=et-local-rs&tls=true&retryWrites=true"
    }
    if(-not $process.Start()){throw 'Start failed'}
    $processStarted=$true
    $errors=$process.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 8
    if($process.HasExited){throw "Server exited before test: $($process.ExitCode)"}
    $process.StandardInput.WriteLine($(if($SelfCredentials){'gameplay-check self-credentials'}elseif($Credentials){'gameplay-check credentials'}elseif($LeaseExpiry){'gameplay-check lease-expiry'}elseif($Governance){'gameplay-check governance'}elseif($Resume){'gameplay-check resume'}elseif($SaveFailure){'gameplay-check save-failure'}elseif($Outage){'gameplay-check outage'}elseif($Baseline){'gameplay-check baseline'}else{'gameplay-check'}))
    $deadline=[DateTime]::UtcNow.AddSeconds($(if($Baseline){240+$DurationSeconds+[Math]::Ceiling($Clients/20.0)*90}else{240}))
    $passed=$false
    $lastResourceCheck=[DateTime]::MinValue
    while([DateTime]::UtcNow -lt $deadline){
        $lineTask=$process.StandardOutput.ReadLineAsync()
        if(-not $lineTask.Wait([TimeSpan]::FromSeconds(60))){throw 'Gameplay output timed out'}
        $line=$lineTask.Result
        if($null -eq $line){break}
        if($line -match 'GAMEPLAY_'){$lines.Add($line);Write-Host $line}
        if($CapacityProfile -and ([DateTime]::UtcNow-$lastResourceCheck).TotalSeconds -ge 5){
            $lastResourceCheck=[DateTime]::UtcNow
            $process.Refresh()
            if($process.WorkingSet64 -gt 8GB -or (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt 2MB -or
               ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($repo))).AvailableFreeSpace -lt 5GB){
                throw 'Local capacity resource guard reached (8 GiB process, less than 2 GiB free RAM or 5 GiB free workspace disk)'
            }
        }
        if($Outage -and $line -match 'GAMEPLAY_OUTAGE_STOP'){
            & "$PSScriptRoot/LocalMongo.ps1" -Action Crash -EnvironmentRoot $EnvironmentRoot
            'stopped' | Set-Content "$env:ET_LOCAL_MONGO_ROOT/outage-stopped"
        }
        if($Outage -and $line -match 'GAMEPLAY_OUTAGE_RESTORE'){
            & "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
            'restored' | Set-Content "$env:ET_LOCAL_MONGO_ROOT/outage-restored"
        }
        if($line -match 'GAMEPLAY_ROLE (\d+)'){$env:ET_GAMEPLAY_ROLEID=$Matches[1]}
        if($line -match 'GAMEPLAY_RESULT FAIL'){throw $line}
        if($CrashJournal -and $line -match "GAMEPLAY_PROGRESS online=$Clients sustained_ms="){$passed=$true;break}
        if($SaveFailure -and $line -match 'GAMEPLAY_EXPECT_SAVE_FAILURE'){$passed=$true;break}
        if($line -match 'GAMEPLAY_RESULT PASS'){$passed=$true;break}
    }
    if(-not $passed){throw 'Gameplay acceptance did not complete'}
    if($CrashJournal){
        $previousCrashClients=$env:ET_GAMEPLAY_CLIENTS
        try {
            $env:ET_GAMEPLAY_CLIENTS=[string]$Clients
            $beforeOutput=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Snapshot-GameplayJournal.js"
            if($LASTEXITCODE -ne 0){throw 'Pre-crash journal snapshot failed'}
            $beforeCrash=($beforeOutput -join "`n")|ConvertFrom-Json
            if(-not $beforeCrash.Passed -or $process.HasExited){throw 'Owned live server and complete admitted cohort required'}
            $startedAt=$process.StartTime.ToUniversalTime().ToString('O')
            $ownedPid=$process.Id
            $crashWatch=[Diagnostics.Stopwatch]::StartNew()
            # The handle comes from this invocation's Process.Start, never a PID read from a file.
            $process.Kill($true)
            if(-not $process.WaitForExit(10000)){throw 'Owned process exit not observed after intentional crash'}
            $crashExitObserved=$true
            $observedAt=[DateTime]::UtcNow.ToString('O')
            $crashExit=$process.ExitCode
            $crashElapsed=$crashWatch.ElapsedMilliseconds
            $afterOutput=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Snapshot-GameplayJournal.js"
            if($LASTEXITCODE -ne 0){throw 'Post-crash journal snapshot failed'}
            $afterCrash=($afterOutput -join "`n")|ConvertFrom-Json
            if(-not $afterCrash.Passed -or $beforeCrash.SnapshotSha256 -ne $afterCrash.SnapshotSha256){throw 'Crash did not preserve the expected unfinished journal cohort'}
            $crashReport=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));
                CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;Test='owned-process-crash-journal';Clients=$Clients;
                Cohort=$env:ET_GAMEPLAY_ACCOUNT;HostProcessId=$ownedPid;ProcessStartedAtUtc=$startedAt;ExitObservedAtUtc=$observedAt;
                IntentionalCrash=$true;ExitObserved=$true;ExitCode=$crashExit;ExitElapsedMs=$crashElapsed;
                Before=$beforeCrash;After=$afterCrash;RecoveryVerified=$false;ProductionCapacityVerified=$false}
            $crashPath="$env:ET_LOCAL_MONGO_ROOT/evidence/journal-crash-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
            $crashReport|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $crashPath
            Write-Host "GAMEPLAY_PASS owned-process-crash-journal records=$Clients recovery_verified=false"
            Write-Host "Evidence: $crashPath"
        } finally {$env:ET_GAMEPLAY_CLIENTS=$previousCrashClients}
        return
    }
    $drain=$process.StandardOutput.ReadToEndAsync()
    if(-not $SaveFailure){$process.StandardInput.WriteLine('shutdown')}
    $expectedExit=if($SaveFailure){2}else{0}
    $shutdownWatch=[Diagnostics.Stopwatch]::StartNew()
    $shutdownObserved=$process.WaitForExit(25000)
    $shutdownElapsedMs=$shutdownWatch.ElapsedMilliseconds
    if($shutdownObserved){$shutdownExitCode=$process.ExitCode}
    if(-not $shutdownObserved){throw 'Gameplay server did not exit within the 25-second shutdown observation window'}
    if($shutdownExitCode -ne $expectedExit){throw "Gameplay server exited with code $shutdownExitCode; expected $expectedExit"}
    if($SaveFailure){$lines.Add('GAMEPLAY_PASS storage-failure-shutdown-exit-2');Write-Host 'GAMEPLAY_PASS storage-failure-shutdown-exit-2'}
    if($Baseline){
        $previousJournalClients=$env:ET_GAMEPLAY_CLIENTS
        try {
            $env:ET_GAMEPLAY_CLIENTS=[string]$Clients
            $journalOutput=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Verify-GameplayJournal.js"
            $journalExit=$LASTEXITCODE
            $journalVerification=($journalOutput -join "`n")|ConvertFrom-Json
            if($journalExit -ne 0 -or -not $journalVerification.Passed){throw 'Post-shutdown session journal verification failed'}
            $journalCheck="GAMEPLAY_PASS shutdown-journal-all-ended records=$($journalVerification.Records)"
            $lines.Add($journalCheck);Write-Host $journalCheck
        } finally {$env:ET_GAMEPLAY_CLIENTS=$previousJournalClients}
    }
    $report=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;Clients=$Clients;DurationSeconds=$DurationSeconds;Checks=@($lines|Where-Object{$_ -match 'GAMEPLAY_PASS'});Samples=@($lines|Where-Object{$_ -match 'GAMEPLAY_PROGRESS'});ProductionCapacityVerified=$false}
    $report.CapacityProfile=[bool]$CapacityProfile
    $report.JournalVerification=$journalVerification
    $report.LogProfile=$info.Environment['ET_LOG_PROFILE'];$report.HostProcessId=$process.Id
    $report.ShutdownObserved=$shutdownObserved;$report.ShutdownExitCode=$shutdownExitCode;$report.ShutdownElapsedMs=$shutdownElapsedMs
    $report.ProbeConcurrency=$(if($CapacityProfile){20}else{1})
    $report.LoginIpLimitOverride=$(if($CapacityProfile){$Clients}else{$null})
    $report | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/$(if($SelfCredentials){'gameplay-self-credentials'}elseif($Credentials){'gameplay-credentials'}elseif($LeaseExpiry){'gameplay-lease-expiry'}elseif($Governance){'gameplay-governance'}elseif($Resume){'gameplay-restart'}elseif($SaveFailure){'gameplay-save-failure'}elseif($Outage){'gameplay-outage'}elseif($Baseline){'gameplay-baseline'}else{'gameplay'}).json"
    $report | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/gameplay-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
} catch {
    $failure=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$false;Clients=$Clients;DurationSeconds=$DurationSeconds;ProductionCapacityVerified=$false;Checks=@($lines|Where-Object{$_ -match 'GAMEPLAY_(PASS|PROGRESS|RESULT)'})}
    $failure.CapacityProfile=[bool]$CapacityProfile
    $failure.JournalVerification=$journalVerification
    $failure.CrashJournal=[bool]$CrashJournal;$failure.CrashExitObserved=$crashExitObserved;$failure.CrashExitCode=$crashExit
    $failure.LogProfile=$info.Environment['ET_LOG_PROFILE'];$failure.HostProcessId=$(if($processStarted){$process.Id}else{$null})
    $failure.ShutdownObserved=$shutdownObserved;$failure.ShutdownExitCode=$shutdownExitCode;$failure.ShutdownElapsedMs=$shutdownElapsedMs
    $failure.ProbeConcurrency=$(if($CapacityProfile){20}else{1})
    $failure.LoginIpLimitOverride=$(if($CapacityProfile){$Clients}else{$null})
    $failure | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/gameplay-failed-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
    throw
} finally {
    if($processStarted -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $process.Dispose()
    if($selfRoleCreated){
        try {
            $env:ET_SELF_TEST_CLEANUP='1'
            & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalSelfPasswordRole.js"
            if($LASTEXITCODE -ne 0){throw 'Temporary self-password role cleanup failed'}
        } finally {Remove-Item Env:ET_SELF_TEST_USER,Env:ET_SELF_TEST_PASSWORD,Env:ET_SELF_TEST_CLEANUP -ErrorAction SilentlyContinue}
    }
    if($Outage){& "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot}
}
if(-not $Resume -and -not $Outage -and -not $Baseline -and -not $Governance -and -not $Credentials -and -not $SelfCredentials){
    try { & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Resume -BoundedLogs:$BoundedLogs; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Outage -BoundedLogs:$BoundedLogs; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -SaveFailure -BoundedLogs:$BoundedLogs; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Baseline -BoundedLogs:$BoundedLogs }
    finally { Remove-Item Env:ET_GAMEPLAY_PASSWORD,Env:ET_GAMEPLAY_ACCOUNT,Env:ET_GAMEPLAY_ROLEID -ErrorAction SilentlyContinue }
}

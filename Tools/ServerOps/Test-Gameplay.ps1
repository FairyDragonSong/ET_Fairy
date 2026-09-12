param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[switch]$Resume,[switch]$Outage,[switch]$SaveFailure,[switch]$Baseline,
    [ValidateRange(1,3000)][int]$Clients=20,[ValidateRange(0,86400)][int]$DurationSeconds=0)
$ErrorActionPreference='Stop'
if(($Clients -ne 20 -or $DurationSeconds -ne 0) -and -not $Baseline){throw 'Load parameters require -Baseline'}
if(@($Resume,$Outage,$SaveFailure,$Baseline).Where({$_}).Count -gt 1){throw 'Select only one test mode'}
if($SaveFailure){$Outage=$true}
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
$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.Environment['ET_GAMEPLAY_CLIENTS']=[string]$Clients
$info.Environment['ET_GAMEPLAY_DURATION']=[string]$DurationSeconds
foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
$process=[Diagnostics.Process]::new();$process.StartInfo=$info
$lines=[Collections.Generic.List[string]]::new()
try {
    if(-not $process.Start()){throw 'Start failed'}
    $errors=$process.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 8
    if($process.HasExited){throw "Server exited before test: $($process.ExitCode)"}
    $process.StandardInput.WriteLine($(if($Resume){'gameplay-check resume'}elseif($SaveFailure){'gameplay-check save-failure'}elseif($Outage){'gameplay-check outage'}elseif($Baseline){'gameplay-check baseline'}else{'gameplay-check'}))
    $deadline=[DateTime]::UtcNow.AddSeconds($(if($Baseline){240+$DurationSeconds+[Math]::Ceiling($Clients/20.0)*90}else{240}))
    $passed=$false
    while([DateTime]::UtcNow -lt $deadline){
        $lineTask=$process.StandardOutput.ReadLineAsync()
        if(-not $lineTask.Wait([TimeSpan]::FromSeconds(60))){throw 'Gameplay output timed out'}
        $line=$lineTask.Result
        if($null -eq $line){break}
        if($line -match 'GAMEPLAY_'){$lines.Add($line);Write-Host $line}
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
        if($SaveFailure -and $line -match 'GAMEPLAY_EXPECT_SAVE_FAILURE'){$passed=$true;break}
        if($line -match 'GAMEPLAY_RESULT PASS'){$passed=$true;break}
    }
    if(-not $passed){throw 'Gameplay acceptance did not complete'}
    $drain=$process.StandardOutput.ReadToEndAsync()
    if(-not $SaveFailure){$process.StandardInput.WriteLine('shutdown')}
    $expectedExit=if($SaveFailure){2}else{0}
    if(-not $process.WaitForExit(25000) -or $process.ExitCode -ne $expectedExit){throw "Gameplay server did not return expected shutdown exit code $expectedExit"}
    if($SaveFailure){$lines.Add('GAMEPLAY_PASS storage-failure-shutdown-exit-2');Write-Host 'GAMEPLAY_PASS storage-failure-shutdown-exit-2'}
    $report=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;Clients=$Clients;DurationSeconds=$DurationSeconds;Checks=@($lines|Where-Object{$_ -match 'GAMEPLAY_PASS'});Samples=@($lines|Where-Object{$_ -match 'GAMEPLAY_PROGRESS'});ProductionCapacityVerified=$false}
    $report | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/$(if($Resume){'gameplay-restart'}elseif($SaveFailure){'gameplay-save-failure'}elseif($Outage){'gameplay-outage'}elseif($Baseline){'gameplay-baseline'}else{'gameplay'}).json"
    $report | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/gameplay-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
} catch {
    $failure=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$false;Clients=$Clients;DurationSeconds=$DurationSeconds;ProductionCapacityVerified=$false;Checks=@($lines|Where-Object{$_ -match 'GAMEPLAY_(PASS|PROGRESS|RESULT)'})}
    $failure | ConvertTo-Json -Depth 4 | Set-Content "$env:ET_LOCAL_MONGO_ROOT/evidence/gameplay-failed-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')).json"
    throw
} finally {
    if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $process.Dispose()
    if($Outage){& "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot}
}
if(-not $Resume -and -not $Outage -and -not $Baseline){
    try { & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Resume; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Outage; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -SaveFailure; & $PSCommandPath -EnvironmentRoot $EnvironmentRoot -Baseline }
    finally { Remove-Item Env:ET_GAMEPLAY_PASSWORD,Env:ET_GAMEPLAY_ACCOUNT,Env:ET_GAMEPLAY_ROLEID -ErrorAction SilentlyContinue }
}

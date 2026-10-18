param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')

$ErrorActionPreference='Stop'
. "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$env:ET_MONGO_DATABASE='ET_LOCAL_GAMEPLAY'
$env:ET_GAMEPLAY_LOOPBACK='1'
$account='gate_fail_'+[Guid]::NewGuid().ToString('N').Substring(0,16)
$password=[Guid]::NewGuid().ToString('N')
$marker=Join-Path $env:ET_LOCAL_MONGO_ROOT ('gate-failover-'+[Guid]::NewGuid().ToString('N')+'.marker')
$ports=@(20001,20002,20003,20004,30002,30003,30004)
$udp=@((Get-NetUDPEndpoint).LocalPort);$tcp=@((Get-NetTCPConnection -State Listen).LocalPort)
foreach($port in $ports){if($port -in $udp -or $port -in $tcp){throw "Port already occupied: $port"}}

$processes=@{}
# 启动隔离的测试服务进程。
function Start-TestProcess([int]$number){
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
    $info.WorkingDirectory="$repo/Bin"
    $console=if($number -eq 1){1}else{0}
    $info.Arguments="--Process=$number --StartConfig=StartConfig/Release --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1 --Console=$console --ShutdownTimeoutSeconds=10"
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['ET_GAMEPLAY_ACCOUNT']=$account;$info.Environment['ET_GAMEPLAY_PASSWORD']=$password;$info.Environment['ET_GATE_FAILOVER_MARKER']=$marker
    foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    if(-not $process.Start()){throw "Failed to start process $number"}
    $processes[$number]=$process
}

try{
    foreach($number in @(4,2,3,1)){Start-TestProcess $number}
    Start-Sleep -Seconds 8
    foreach($entry in $processes.GetEnumerator()){if($entry.Value.HasExited){throw "Process $($entry.Key) exited early: $($entry.Value.ExitCode)"}}
    $realm=$processes[1];$errors=$realm.StandardError.ReadToEndAsync()
    $realm.StandardInput.WriteLine('startup-check');$realm.StandardInput.Flush()
    $readyDeadline=[DateTime]::UtcNow.AddSeconds(30);$ready=$false
    while([DateTime]::UtcNow -lt $readyDeadline){
        $read=$realm.StandardOutput.ReadLineAsync();if(-not $read.Wait([TimeSpan]::FromSeconds(20))){break}
        if($null -eq $read.Result){break};if($read.Result -match 'STARTUP_READY'){$ready=$true;break}
    }
    if(-not $ready){throw 'Release Realm console did not report startup readiness'}
    $realm.StandardInput.WriteLine('gameplay-check gate-failover');$realm.StandardInput.Flush()
    $deadline=[DateTime]::UtcNow.AddSeconds(45);$passed=$false;$killedProcess=$null;$killedGate=$null;$fallbackGate=$null
    while([DateTime]::UtcNow -lt $deadline){
        $read=$realm.StandardOutput.ReadLineAsync();if(-not $read.Wait([TimeSpan]::FromSeconds(20))){throw 'Gate failover output timed out'}
        $line=$read.Result;if($null -eq $line){break}
        if($line -match 'GAMEPLAY_GATE_KILL process=(\d+) gate=(\d+)$'){
            $killedProcess=[int]$Matches[1];$killedGate=[int]$Matches[2]
            if($killedProcess -notin @(2,3)){throw "Refusing to kill unexpected process $killedProcess"}
            $target=$processes[$killedProcess];if($target.HasExited){throw 'Target Gate exited before controlled kill'}
            $target.Kill($true);if(-not $target.WaitForExit(10000)){throw 'Killed Gate exit was not observed'}
            [IO.File]::WriteAllText($marker,'killed')
        }
        if($line -match 'GAMEPLAY_PASS gate-process-failover killed_gate=(\d+) fallback_gate=(\d+)$'){$fallbackGate=[int]$Matches[2];Write-Host $line}
        if($line -match 'GAMEPLAY_RESULT FAIL'){throw $line}
        if($line -match 'GAMEPLAY_RESULT PASS$'){$passed=$true;break}
    }
    if(-not $passed -or $null -eq $killedProcess -or $fallbackGate -eq $killedGate){throw 'Independent Gate failover was not proven'}
    $evidence=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;KilledProcess=$killedProcess;KilledGate=$killedGate;FallbackGate=$fallbackGate;ReplayRejected=$true;ProductionHighAvailabilityVerified=$false}
    $path=Join-Path $env:ET_LOCAL_MONGO_ROOT 'evidence/gate-failover.json';$evidence|ConvertTo-Json|Set-Content -LiteralPath $path
    Write-Host "Gate failover evidence: $path"
}
finally{
    foreach($entry in @($processes.GetEnumerator())){
        $process=$entry.Value
        try{if(-not $process.HasExited){$process.StandardInput.WriteLine('shutdown');$process.StandardInput.Flush();if(-not $process.WaitForExit(15000)){$process.Kill($true);$process.WaitForExit()}}}finally{$process.Dispose()}
    }
    if(Test-Path -LiteralPath $marker){Remove-Item -LiteralPath $marker -Force}
}

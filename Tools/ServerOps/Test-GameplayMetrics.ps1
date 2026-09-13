param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[switch]$Notifications)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$promRoot=Join-Path $EnvironmentRoot 'prometheus-3.13.3/prometheus-3.13.3.windows-amd64'
if(-not(Test-Path "$promRoot/prometheus.exe")){throw 'Install the pinned local Prometheus first'}
$amRoot=Join-Path $EnvironmentRoot 'alertmanager-0.34.0/alertmanager-0.34.0.windows-amd64'
if($Notifications -and -not(Test-Path "$amRoot/alertmanager.exe")){throw 'Install the pinned local Alertmanager first'}
$tcpPorts=@((Get-NetTCPConnection -State Listen).LocalPort)
$udpPorts=@((Get-NetUDPEndpoint).LocalPort)
$ports=@(30491,30909,20001,30002,30003,30004,30300,30301,30302,30303,30304)
if($Notifications){$ports+=@(30910,30911)}
foreach($port in $ports){
    if($port -in $tcpPorts -or $port -in $udpPorts){throw "Test port occupied: $port"}
}
. "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
$fixture=Join-Path ([IO.Path]::GetFullPath($EnvironmentRoot)) ('gameplay-metrics-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $fixture /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F'|Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot protect fixture credentials'}
$tokenFile=Join-Path $fixture 'token.txt'
$app=$null;$collector=$null
$appStarted=$false;$collectorStarted=$false
$manager=$null;$receiver=$null;$managerStarted=$false;$receiverStarted=$false
$alertTokenFile=Join-Path $fixture 'alert-token.txt'
$notificationEvidence=[Collections.Generic.List[object]]::new()
$notificationVerified=$false
$queries=[Collections.Generic.List[object]]::new()
$checks=[Collections.Generic.List[string]]::new()
$passed=$false
function Wait-Query([string]$Expression,[string]$Expected){
    for($attempt=0;$attempt -lt 40;$attempt++){
        if($app.HasExited -or $collector.HasExited){throw 'Test process exited during collection'}
        try {
            $reply=Invoke-RestMethod -NoProxy -TimeoutSec 3 -Uri ('http://127.0.0.1:30909/api/v1/query?query='+[Uri]::EscapeDataString($Expression))
            $values=@($reply.data.result)
            if($reply.status -eq 'success' -and $values.Count -eq 1 -and $values[0].value[1] -eq $Expected){
                $queries.Add([ordered]@{Expression=$Expression;Value=$values[0].value[1];SampleTimestamp=$values[0].value[0];ObservedAtUtc=[DateTime]::UtcNow.ToString('O')})
                return
            }
        }catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Query did not reach expected value: $Expression"
}
function Wait-Notification([string]$Status,[string]$Fingerprint=''){
    for($attempt=0;$attempt -lt 40;$attempt++){
        if($manager.HasExited -or $receiver.HasExited){throw 'Notification process exited'}
        $path=Join-Path $fixture 'received-alerts.jsonl'
        if(Test-Path -LiteralPath $path){
            try {
                $events=@(Get-Content -LiteralPath $path|ForEach-Object {$_|ConvertFrom-Json})
                $event=$events|Where-Object {$_.Alertname -eq 'ETLocalMapMailboxBacklog' -and $_.Job -eq 'et-gameplay' -and
                    $_.Status -eq $Status -and $_.Fingerprint -match '^[a-f0-9]{16}$' -and (-not $Fingerprint -or $_.Fingerprint -eq $Fingerprint)}|Select-Object -First 1
                if($event){$notificationEvidence.Add($event);return $event}
            }catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "Expected local $Status notification did not arrive"
}
try {
    [IO.File]::WriteAllText($tokenFile,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $config=Join-Path $fixture 'prometheus.json'
    $promConfig=@{global=@{scrape_interval='1s';scrape_timeout='500ms';evaluation_interval='1s'};
      rule_files=@("$PSScriptRoot/monitoring/et-gameplay-test.rules.yml".Replace('\','/'));
      scrape_configs=@(@{job_name='et-gameplay';authorization=@{type='Bearer';credentials_file=$tokenFile.Replace('\','/')};static_configs=@(@{targets=@('127.0.0.1:30491')})})}
    if($Notifications){
        [IO.File]::WriteAllText($alertTokenFile,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
        $receiverInfo=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $receiverInfo.UseShellExecute=$false;$receiverInfo.CreateNoWindow=$true
        $receiverInfo.RedirectStandardOutput=$true;$receiverInfo.RedirectStandardError=$true
        foreach($argument in @('-NoProfile','-File',"$PSScriptRoot/Receive-LocalAlerts.ps1")){$receiverInfo.ArgumentList.Add($argument)}
        $receiverInfo.Environment['ET_ALERT_TEST_DIRECTORY']=$fixture
        $receiverInfo.Environment['ET_ALERT_TEST_REJECT_ONCE']='0'
        $receiver=[Diagnostics.Process]::new();$receiver.StartInfo=$receiverInfo
        if(-not $receiver.Start()){throw 'Receiver start failed'}
        $receiverStarted=$true
        $receiverOutput=$receiver.StandardOutput.ReadToEndAsync();$receiverError=$receiver.StandardError.ReadToEndAsync()
        $amConfig=Join-Path $fixture 'alertmanager.json'
        @{route=@{receiver='local-test';group_by=@('alertname','job');group_wait='1s';group_interval='1s';repeat_interval='1m'};
          receivers=@(@{name='local-test';webhook_configs=@(@{url='http://127.0.0.1:30911/alerts';send_resolved=$true;
            http_config=@{authorization=@{type='Bearer';credentials_file=$alertTokenFile.Replace('\','/')}}})})}|ConvertTo-Json -Depth 12|Set-Content $amConfig
        & "$amRoot/amtool.exe" check-config $amConfig
        if($LASTEXITCODE -ne 0){throw 'Alertmanager config invalid'}
        $managerInfo=[Diagnostics.ProcessStartInfo]::new("$amRoot/alertmanager.exe")
        $managerInfo.UseShellExecute=$false;$managerInfo.CreateNoWindow=$true
        $managerInfo.RedirectStandardOutput=$true;$managerInfo.RedirectStandardError=$true
        foreach($argument in @("--config.file=$amConfig",'--web.listen-address=127.0.0.1:30910','--cluster.listen-address=',"--storage.path=$fixture/alertmanager-data")){$managerInfo.ArgumentList.Add($argument)}
        $manager=[Diagnostics.Process]::new();$manager.StartInfo=$managerInfo
        if(-not $manager.Start()){throw 'Alertmanager start failed'}
        $managerStarted=$true
        $managerOutput=$manager.StandardOutput.ReadToEndAsync();$managerError=$manager.StandardError.ReadToEndAsync()
        $promConfig.alerting=@{alertmanagers=@(@{static_configs=@(@{targets=@('127.0.0.1:30910')})})}
    }
    $promConfig|ConvertTo-Json -Depth 10|Set-Content $config
    & "$promRoot/promtool.exe" check config $config
    if($LASTEXITCODE -ne 0){throw 'Prometheus config invalid'}
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
    $info.WorkingDirectory="$repo/Bin";$info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Arguments='--Process=1 --StartConfig=StartConfig/Localhost --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1 --Console=1 --ShutdownTimeoutSeconds=20'
    foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
    $info.Environment['ET_MONGO_DATABASE']='ET_LOCAL_GAMEPLAY'
    $info.Environment['ET_GAMEPLAY_LOOPBACK']='1'
    $info.Environment['ET_GAMEPLAY_ACCOUNT']='e2e_'+[Guid]::NewGuid().ToString('N').Substring(0,16)
    $info.Environment['ET_GAMEPLAY_PASSWORD']=[Guid]::NewGuid().ToString('N')
    $info.Environment['ET_GAMEPLAY_CLIENTS']='20';$info.Environment['ET_GAMEPLAY_DURATION']='10'
    $info.Environment['ET_METRICS_PORT']='30491';$info.Environment['ET_METRICS_TOKEN_FILE']=$tokenFile
    $info.Environment['ET_GAMEPLAY_METRICS_FIXTURE']=$fixture
    $app=[Diagnostics.Process]::new();$app.StartInfo=$info
    if(-not $app.Start()){throw 'Gameplay host start failed'}
    $appStarted=$true
    $appError=$app.StandardError.ReadToEndAsync()
    $prom=[Diagnostics.ProcessStartInfo]::new("$promRoot/prometheus.exe")
    $prom.UseShellExecute=$false;$prom.CreateNoWindow=$true
    $prom.RedirectStandardOutput=$true;$prom.RedirectStandardError=$true
    foreach($argument in @("--config.file=$config",'--web.listen-address=127.0.0.1:30909',"--storage.tsdb.path=$fixture/tsdb",'--storage.tsdb.retention.time=1d','--storage.tsdb.retention.size=128MB')){$prom.ArgumentList.Add($argument)}
    $collector=[Diagnostics.Process]::new();$collector.StartInfo=$prom
    if(-not $collector.Start()){throw 'Collector start failed'}
    $collectorStarted=$true
    $promOutput=$collector.StandardOutput.ReadToEndAsync();$promError=$collector.StandardError.ReadToEndAsync()
    Wait-Query 'up{job="et-gameplay"}' '1'
    Wait-Query 'et_startup_ready{job="et-gameplay"}' '1'
    Wait-Query 'et_process_id{job="et-gameplay"}' ([string]$app.Id)
    $app.StandardInput.WriteLine('gameplay-check metrics')
    $held=$false;$recovered=$false;$done=$false
    $deadline=[DateTime]::UtcNow.AddMinutes(4)
    while([DateTime]::UtcNow -lt $deadline){
        $lineTask=$app.StandardOutput.ReadLineAsync()
        if(-not $lineTask.Wait([TimeSpan]::FromSeconds(45))){throw 'Gameplay output timed out'}
        $line=$lineTask.Result
        if($null -eq $line){break}
        if($line -match 'GAMEPLAY_'){Write-Host $line}
        if($line -match 'GAMEPLAY_PASS'){$checks.Add($line)}
        if($line -match 'GAMEPLAY_RESULT FAIL'){throw 'Gameplay acceptance failed'}
        if($line -match 'GAMEPLAY_METRICS_HELD'){
            Wait-Query 'et_map_entry_handler_seconds_count{job="et-gameplay"} > bool 0' '1'
            Wait-Query 'et_map_ordered_mailbox_seconds_count{job="et-gameplay"} > bool 0' '1'
            Wait-Query 'et_map_unordered_mailbox_seconds_count{job="et-gameplay"} > bool 0' '1'
            Wait-Query 'et_map_ordered_mailbox_inflight{job="et-gameplay"} > bool 0' '1'
            Wait-Query 'ALERTS{alertname="ETLocalMapMailboxBacklog",job="et-gameplay",alertstate="firing"}' '1'
            if($Notifications){$firing=Wait-Notification 'firing'}
            $held=$true
            [IO.File]::WriteAllText((Join-Path $fixture 'release'),'release')
        }
        if($line -match 'GAMEPLAY_METRICS_RELEASED'){
            if(-not $held){throw 'Recovery arrived before verified firing'}
            Wait-Query 'up{job="et-gameplay"}' '1'
            Wait-Query 'et_map_ordered_mailbox_inflight{job="et-gameplay"}' '0'
            Wait-Query '(sum(et_map_ordered_mailbox_seconds_bucket{job="et-gameplay",le="+Inf"}) - sum(et_map_ordered_mailbox_seconds_bucket{job="et-gameplay",le="0.1"})) > bool 0' '1'
            Wait-Query 'et_map_ordered_mailbox_exceptions_total{job="et-gameplay"} > bool 0' '1'
            Wait-Query 'et_map_unordered_mailbox_exceptions_total{job="et-gameplay"} > bool 0' '1'
            Wait-Query '(count(ALERTS{alertname="ETLocalMapMailboxBacklog",job="et-gameplay",alertstate="firing"}) or vector(0))' '0'
            if($Notifications){
                $resolved=Wait-Notification 'resolved' $firing.Fingerprint
                if([DateTime]$resolved.ReceivedAtUtc -lt [DateTime]$firing.ReceivedAtUtc){throw 'Notification order incorrect'}
                $notificationVerified=$true
            }
            $recovered=$true
            [IO.File]::WriteAllText((Join-Path $fixture 'finish'),'finish')
        }
        if($line -match 'GAMEPLAY_RESULT PASS'){$done=$true;break}
    }
    if(-not($done -and $held -and $recovered)){throw 'Gameplay monitoring sequence incomplete'}
    $drain=$app.StandardOutput.ReadToEndAsync()
    $app.StandardInput.WriteLine('shutdown')
    if(-not $app.WaitForExit(25000) -or $app.ExitCode -ne 0){throw 'Gameplay host shutdown failed'}
    $passed=$true
    Write-Host 'PASS real-gameplay-scrape-mailbox-firing-and-recovery'
    if($Notifications){Write-Host 'PASS map-backlog-firing-and-matching-recovery-webhook-delivered'}
} finally {
    foreach($entry in @(@{Process=$app;Started=$appStarted},@{Process=$collector;Started=$collectorStarted},
        @{Process=$manager;Started=$managerStarted},@{Process=$receiver;Started=$receiverStarted})){
        $child=$entry.Process
        if($null -ne $child){
            try {if($entry.Started -and -not $child.HasExited){$child.Kill($true);$child.WaitForExit()}}finally{$child.Dispose()}
        }
    }
    # Exact credential file in this run's newly created fixture; retain non-secret evidence.
    if(Test-Path -LiteralPath $tokenFile){Remove-Item -LiteralPath $tokenFile}
    if(Test-Path -LiteralPath $alertTokenFile){Remove-Item -LiteralPath $alertTokenFile}
    [ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=([bool](& git -C $repo status --porcelain));Passed=$passed;
        CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Checks=@($checks);Queries=@($queries);ProductionCapacityVerified=$false;
        CrossHostVerified=$false;ExternalNotificationVerified=$false;LocalNotificationVerified=$notificationVerified;
        Notifications=@($notificationEvidence)}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $fixture 'result.json')
}

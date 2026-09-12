param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[ValidateRange(1024,65535)][int]$Port=30491,[ValidateRange(1024,65535)][int]$PeerPort=30492,[switch]$Prometheus,[switch]$Alertmanager,[switch]$NotificationRetry)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
if($NotificationRetry){$Alertmanager=$true}
if($Alertmanager){
    $Prometheus=$true
    foreach($auxPort in @(30910,30911)){
        if($Port -eq $auxPort -or $PeerPort -eq $auxPort -or (Get-NetTCPConnection -LocalPort $auxPort -State Listen -ErrorAction SilentlyContinue)){throw 'Alert test ports unavailable'}
    }
    $amRoot="$EnvironmentRoot/alertmanager-0.34.0/alertmanager-0.34.0.windows-amd64"
    if(-not(Test-Path "$amRoot/alertmanager.exe")){throw 'Run Install-LocalAlertmanager.ps1 first'}
}
if($Prometheus){
    if($Port -eq 30909 -or $PeerPort -eq 30909 -or (Get-NetTCPConnection -LocalPort 30909 -State Listen -ErrorAction SilentlyContinue)){throw 'Collector test port 30909 unavailable'}
    $promRoot="$EnvironmentRoot/prometheus-3.13.3/prometheus-3.13.3.windows-amd64"
    if(-not (Test-Path "$promRoot/prometheus.exe")){throw 'Run Install-LocalPrometheus.ps1 first'}
}
if(@($Port,$PeerPort,30300,30301,20008,20009|Select-Object -Unique).Count -ne 6){throw 'Metrics test ports must be distinct from each other and service ports'}
foreach($testPort in @($Port,$PeerPort,30300,30301,20008,20009)){
    if(Get-NetTCPConnection -LocalPort $testPort -State Listen -ErrorAction SilentlyContinue){throw "Test port occupied: $testPort"}
    if(Get-NetUDPEndpoint -LocalPort $testPort -ErrorAction SilentlyContinue){throw "Test UDP port occupied: $testPort"}
}
$fixture=Join-Path $EnvironmentRoot ('metrics-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $fixture /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot protect metrics test credentials'}
$tokenFile=Join-Path $fixture 'token.txt'
$token=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
[IO.File]::WriteAllText($tokenFile,$token)
$process=$null
$peer=$null
$collector=$null
$manager=$null
$receiver=$null
$alertTokenFile=Join-Path $fixture 'alert-token.txt'
$peerTokenFile=Join-Path $fixture 'peer-token.txt'
$peerToken=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
[IO.File]::WriteAllText($peerTokenFile,$peerToken)
$handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false
$client=[Net.Http.HttpClient]::new($handler)
$client.Timeout=[TimeSpan]::FromSeconds(3)
$checks=[Collections.Generic.List[string]]::new()
$queryEvidence=[Collections.Generic.List[object]]::new()
function Start-TestProcess([string]$ConfiguredPort,[string]$CredentialFile,[int]$ProcessNumber=8){
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
    $info.WorkingDirectory="$repo/Bin"
    $info.Arguments="--Process=$ProcessNumber --StartConfig=StartConfig/Release --Console=1 --ShutdownTimeoutSeconds=5"
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment.Remove('ET_METRICS_PORT')|Out-Null
    $info.Environment.Remove('ET_METRICS_TOKEN_FILE')|Out-Null
    if($ConfiguredPort){$info.Environment['ET_METRICS_PORT']=$ConfiguredPort}
    if($CredentialFile){$info.Environment['ET_METRICS_TOKEN_FILE']=$CredentialFile}
    $child=[Diagnostics.Process]::new();$child.StartInfo=$info
    if(-not $child.Start()){throw 'Metrics test process did not start'}
    $script:stdout=$child.StandardOutput.ReadToEndAsync()
    $script:stderr=$child.StandardError.ReadToEndAsync()
    return $child
}
function Request([string]$Path,[string]$Authorization,[string]$Method='GET',[int]$TargetPort=$Port){
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),"http://127.0.0.1:$TargetPort$Path")
    try {
        if($Authorization){$request.Headers.TryAddWithoutValidation('Authorization',$Authorization)|Out-Null}
        return $client.SendAsync($request).GetAwaiter().GetResult()
    } finally {$request.Dispose()}
}
function Counter([string]$Body,[string]$Name){
    if($Body -notmatch "(?m)^$Name (\d+)\r?$"){throw 'Expected process counter missing'}
    return [long]$Matches[1]
}
function PromQuery([string]$Expression){
    $url='http://127.0.0.1:30909/api/v1/query?query='+[Uri]::EscapeDataString($Expression)
    return ($client.GetStringAsync($url).GetAwaiter().GetResult()|ConvertFrom-Json).data.result
}
function Wait-Prom([string]$Expression,[string]$Expected){
    for($attempt=0;$attempt -lt 40;$attempt++){
        if($collector.HasExited){throw 'Collector exited during acceptance'}
        try {
            $values=@(PromQuery $Expression)
            if($values.Count -eq 1 -and $values[0].value[1] -eq $Expected){
                $queryEvidence.Add([ordered]@{Expression=$Expression;Value=$values[0].value[1];SampleTimestamp=$values[0].value[0];ObservedAtUtc=[DateTime]::UtcNow.ToString('O')})
                return
            }
        }catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Collector query did not reach expected state'
}
function Wait-Notification([string]$Status){
    for($attempt=0;$attempt -lt 80;$attempt++){
        if($receiver.HasExited -or $manager.HasExited){throw 'Local notification process exited'}
        $path=Join-Path $fixture 'received-alerts.jsonl'
        if(Test-Path $path){
            $events=@(Get-Content $path|ForEach-Object {$_|ConvertFrom-Json})
            if(@($events|Where-Object {$_.Job -eq 'et-primary' -and $_.Status -eq $Status}).Count){return}
        }
        Start-Sleep -Milliseconds 500
    }
    throw 'Expected webhook notification did not arrive'
}
try {
    $process=Start-TestProcess ([string]$Port) $tokenFile
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do {
        if($process.HasExited){throw 'Metrics process exited before readiness'}
        try {$response=Request '/metrics' "Bearer $token"}catch {$response=$null}
        if($response -and [int]$response.StatusCode -eq 200){break}
        if($response){$response.Dispose()}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    if(-not $response -or [int]$response.StatusCode -ne 200){throw 'Authorized metrics request did not succeed'}
    try {
        $body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if(-not $body.Contains('et_password_change_accepted_total') -or $body.Contains($token)){throw 'Metrics response missing counters or exposes credential'}
    } finally {$response.Dispose()}
    $checks.Add('authorized-scrape-and-no-secret')
    foreach($case in @(@('/metrics','',401,'GET'),@('/metrics','Bearer invalid',401,'GET'),@('/shutdown',"Bearer $token",404,'GET'),@('/metrics?token=x',"Bearer $token",404,'GET'),@('/metrics',"Bearer $token",405,'POST'))){
        $response=Request $case[0] $case[1] $case[3]
        try {if([int]$response.StatusCode -ne $case[2]){throw 'Unexpected metrics access-control response'}}finally{$response.Dispose()}
    }
    $checks.Add('unauthorized-wrong-token-path-query-method-rejected')
    $listeners=@(Get-NetTCPConnection -LocalPort $Port -State Listen)
    if($listeners.Count -eq 0 -or @($listeners|Where-Object LocalAddress -ne '127.0.0.1').Count){throw 'Exporter is not loopback-only'}
    $checks.Add('loopback-only')
    $peer=Start-TestProcess ([string]$PeerPort) $peerTokenFile 9
    $peerBody=''
    for($attempt=0;$attempt -lt 100;$attempt++){
        if($peer.HasExited){throw 'Peer process exited before readiness'}
        try {
            $peerResponse=Request '/metrics' "Bearer $peerToken" 'GET' $PeerPort
            try {if([int]$peerResponse.StatusCode -eq 200){$peerBody=$peerResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()}}finally{$peerResponse.Dispose()}
        }catch { }
        if($peerBody -match '(?m)^et_startup_ready 1$'){break}
        Start-Sleep -Milliseconds 100
    }
    if((Counter $peerBody 'et_startup_ready') -ne 1 -or (Counter $peerBody 'et_process_id') -ne $peer.Id -or (Counter $peerBody 'et_config_process_id') -ne 9){throw 'Peer metrics not from the expected ready process'}
    if((Counter $body 'et_process_id') -ne $process.Id -or (Counter $body 'et_config_process_id') -ne 8){throw 'Primary metric identity incorrect'}
    $wrong=Request '/metrics' "Bearer $token" 'GET' $PeerPort
    try {if([int]$wrong.StatusCode -ne 401){throw 'Primary credential accepted by peer'}}finally{$wrong.Dispose()}
    $wrong=Request '/metrics' "Bearer $peerToken"
    try {if([int]$wrong.StatusCode -ne 401){throw 'Peer credential accepted by primary'}}finally{$wrong.Dispose()}
    $beforePrimary=Counter $body 'et_http_requests_total'
    $beforePeer=Counter $peerBody 'et_http_requests_total'
    $probe=$client.GetAsync('http://127.0.0.1:30300/health/live').GetAwaiter().GetResult();$probe.Dispose()
    Start-Sleep -Milliseconds 200
    $primaryResponse=Request '/metrics' "Bearer $token"
    try {$primaryBody=$primaryResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()}finally{$primaryResponse.Dispose()}
    $peerResponse=Request '/metrics' "Bearer $peerToken" 'GET' $PeerPort
    try {$peerBody=$peerResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()}finally{$peerResponse.Dispose()}
    if((Counter $primaryBody 'et_startup_ready') -ne 1 -or (Counter $peerBody 'et_startup_ready') -ne 1){throw 'Both processes must be ready during the isolation test'}
    if((Counter $primaryBody 'et_http_requests_total') -le $beforePrimary -or (Counter $peerBody 'et_http_requests_total') -ne $beforePeer){throw 'Per-process counter isolation failed'}
    $checks.Add('two-ready-process-identities-credentials-and-counters-isolated')
    if($Alertmanager){
        [IO.File]::WriteAllText($alertTokenFile,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
        $receiverInfo=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $receiverInfo.UseShellExecute=$false;$receiverInfo.CreateNoWindow=$true
        $receiverInfo.RedirectStandardOutput=$true;$receiverInfo.RedirectStandardError=$true
        foreach($argument in @('-NoProfile','-File',"$PSScriptRoot/Receive-LocalAlerts.ps1")){$receiverInfo.ArgumentList.Add($argument)}
        $receiverInfo.Environment['ET_ALERT_TEST_DIRECTORY']=$fixture
        $receiverInfo.Environment['ET_ALERT_TEST_REJECT_ONCE']=$(if($NotificationRetry){'1'}else{'0'})
        $receiver=[Diagnostics.Process]::new();$receiver.StartInfo=$receiverInfo
        if(-not $receiver.Start()){throw 'Receiver start failed'}
        $receiverOutput=$receiver.StandardOutput.ReadToEndAsync();$receiverError=$receiver.StandardError.ReadToEndAsync()
        $amConfig=Join-Path $fixture 'alertmanager.json'
        @{route=@{receiver='local-test';group_by=@('alertname','job');group_wait='1s';group_interval='1s';repeat_interval='1m'};receivers=@(@{name='local-test';webhook_configs=@(@{url='http://127.0.0.1:30911/alerts';send_resolved=$true;http_config=@{authorization=@{type='Bearer';credentials_file=$alertTokenFile.Replace('\','/')}}})})}|ConvertTo-Json -Depth 12|Set-Content $amConfig
        & "$amRoot/amtool.exe" check-config $amConfig
        if($LASTEXITCODE -ne 0){throw 'Alertmanager config invalid'}
        $managerInfo=[Diagnostics.ProcessStartInfo]::new("$amRoot/alertmanager.exe")
        $managerInfo.UseShellExecute=$false;$managerInfo.CreateNoWindow=$true
        $managerInfo.RedirectStandardOutput=$true;$managerInfo.RedirectStandardError=$true
        foreach($argument in @("--config.file=$amConfig",'--web.listen-address=127.0.0.1:30910','--cluster.listen-address=',"--storage.path=$fixture/alertmanager-data")){$managerInfo.ArgumentList.Add($argument)}
        $manager=[Diagnostics.Process]::new();$manager.StartInfo=$managerInfo
        if(-not $manager.Start()){throw 'Alertmanager start failed'}
        $managerOutput=$manager.StandardOutput.ReadToEndAsync();$managerError=$manager.StandardError.ReadToEndAsync()
    }
    if($Prometheus){
        $jobs=@(
            @{job_name='et-primary';authorization=@{type='Bearer';credentials_file=$tokenFile.Replace('\','/')};static_configs=@(@{targets=@("127.0.0.1:$Port")})},
            @{job_name='et-peer';authorization=@{type='Bearer';credentials_file=$peerTokenFile.Replace('\','/')};static_configs=@(@{targets=@("127.0.0.1:$PeerPort")})}
        )
        $config=Join-Path $fixture 'prometheus.json'
        $promConfig=@{global=@{scrape_interval='1s';scrape_timeout='500ms';evaluation_interval='1s'};rule_files=@("$PSScriptRoot/monitoring/et-local.rules.yml".Replace('\','/'));scrape_configs=$jobs}
        if($Alertmanager){$promConfig.alerting=@{alertmanagers=@(@{static_configs=@(@{targets=@('127.0.0.1:30910')})})}}
        $promConfig|ConvertTo-Json -Depth 10|Set-Content $config
        & "$promRoot/promtool.exe" check config $config
        if($LASTEXITCODE -ne 0){throw 'Collector config validation failed'}
        $collectorInfo=[Diagnostics.ProcessStartInfo]::new("$promRoot/prometheus.exe")
        $collectorInfo.UseShellExecute=$false;$collectorInfo.CreateNoWindow=$true
        $collectorInfo.RedirectStandardOutput=$true;$collectorInfo.RedirectStandardError=$true
        foreach($argument in @("--config.file=$config",'--web.listen-address=127.0.0.1:30909',"--storage.tsdb.path=$fixture/tsdb",'--storage.tsdb.retention.time=1d','--storage.tsdb.retention.size=128MB')){$collectorInfo.ArgumentList.Add($argument)}
        $collector=[Diagnostics.Process]::new();$collector.StartInfo=$collectorInfo
        if(-not $collector.Start()){throw 'Collector did not start'}
        $collectorOutput=$collector.StandardOutput.ReadToEndAsync();$collectorError=$collector.StandardError.ReadToEndAsync()
        Wait-Prom 'up{job="et-primary"}' '1'
        Wait-Prom 'up{job="et-peer"}' '1'
        Wait-Prom 'et_process_id{job="et-primary"}' ([string]$process.Id)
        Wait-Prom 'et_process_id{job="et-peer"}' ([string]$peer.Id)
        Wait-Prom 'count(et_gate_login_handler_seconds_bucket{job="et-primary"})' '12'
        Wait-Prom 'et_gate_login_handler_seconds_count{job="et-primary"}' '0'
        Wait-Prom 'et_fiber_message_queues{job="et-primary"} > bool 0' '1'
        Wait-Prom 'et_fiber_messages_pending{job="et-primary"} >= bool et_fiber_messages_max_pending_per_queue{job="et-primary"}' '1'
        Wait-Prom 'et_coroutine_lock_waiting{job="et-primary"} >= bool 0' '1'
        Wait-Prom 'et_coroutine_lock_waits_started_total{job="et-primary"} >= bool 0' '1'
        Wait-Prom 'et_map_ordered_mailbox_seconds_count{job="et-primary"}' '0'
        Wait-Prom 'et_map_unordered_mailbox_seconds_count{job="et-primary"}' '0'
        if(@(Get-NetTCPConnection -LocalPort 30909 -State Listen|Where-Object LocalAddress -ne '127.0.0.1').Count){throw 'Collector is not loopback-only'}
        $checks.Add('prometheus-two-authenticated-targets-and-identities')
        if($Alertmanager){
            $unauthorized=$client.PostAsync('http://127.0.0.1:30911/alerts',[Net.Http.StringContent]::new('{}')).GetAwaiter().GetResult()
            try {if([int]$unauthorized.StatusCode -ne 401){throw 'Webhook receiver accepted missing credentials'}}finally{$unauthorized.Dispose()}
            $checks.Add('webhook-receiver-rejects-unauthorized-post')
        }
    }
    $address=Get-NetIPAddress -AddressFamily IPv4 | Where-Object {$_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred'} | Select-Object -First 1 -ExpandProperty IPAddress
    if(-not $address){throw 'A local non-loopback address is required to verify legacy metrics isolation'}
    $legacy=$client.GetAsync("http://${address}:30300/metrics").GetAwaiter().GetResult()
    try {if([int]$legacy.StatusCode -ne 403){throw 'Legacy wildcard metrics listener accepted non-loopback request'}}finally{$legacy.Dispose()}
    $checks.Add('legacy-metrics-rejects-non-loopback')
    $process.StandardInput.WriteLine('shutdown')
    if(-not $process.WaitForExit(12000) -or $process.ExitCode -ne 0){throw 'Metrics process shutdown failed'}
    $process.Dispose();$process=$null
    if(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue){throw 'Exporter port retained after shutdown'}
    $checks.Add('graceful-shutdown-releases-exporter-port')
    if($Prometheus){
        Wait-Prom 'up{job="et-primary"}' '0'
        Wait-Prom 'ALERTS{alertname="ETLocalTargetDown",job="et-primary",alertstate="firing"}' '1'
        Wait-Prom 'up{job="et-peer"}' '1'
        $checks.Add('prometheus-target-down-and-firing-with-peer-healthy')
        if($Alertmanager){Wait-Notification 'firing';$checks.Add('alertmanager-firing-webhook-delivered')}
        if($NotificationRetry){
            $rejection=Get-Content (Join-Path $fixture 'rejected-alert.json') -Raw|ConvertFrom-Json
            $delivered=@(Get-Content (Join-Path $fixture 'received-alerts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})|
                Where-Object {$_.Job -eq 'et-primary' -and $_.Status -eq 'firing' -and $_.Fingerprint -eq $rejection.Fingerprint}|Select-Object -First 1
            if($rejection.StatusCode -ne 503 -or -not $delivered -or [DateTimeOffset]$rejection.RejectedAtUtc -ge [DateTimeOffset]$delivered.ReceivedAtUtc){throw 'Notification retry did not follow a matching 503 rejection'}
            $checks.Add('alertmanager-retries-same-alert-after-receiver-503')
        }
        $process=Start-TestProcess ([string]$Port) $tokenFile
        Wait-Prom 'up{job="et-primary"}' '1'
        Wait-Prom 'et_process_id{job="et-primary"}' ([string]$process.Id)
        Wait-Prom 'absent(ALERTS{alertname="ETLocalTargetDown",job="et-primary",alertstate="firing"})' '1'
        $checks.Add('prometheus-restarted-target-new-pid-and-alert-cleared')
        if($Alertmanager){
            Wait-Notification 'resolved'
            $notifications=@(Get-Content (Join-Path $fixture 'received-alerts.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
            $resolved=$notifications|Where-Object {$_.Job -eq 'et-primary' -and $_.Status -eq 'resolved'}|Select-Object -First 1
            $firing=$notifications|Where-Object {$_.Job -eq 'et-primary' -and $_.Status -eq 'firing' -and $_.Fingerprint -eq $resolved.Fingerprint}|Select-Object -First 1
            if(-not $firing -or -not $resolved.Fingerprint -or [DateTimeOffset]$firing.ReceivedAtUtc -gt [DateTimeOffset]$resolved.ReceivedAtUtc){throw 'Recovery notification does not match the earlier firing alert'}
            foreach($auxPort in @(30910,30911)){
                if(@(Get-NetTCPConnection -LocalPort $auxPort -State Listen|Where-Object LocalAddress -ne '127.0.0.1').Count){throw 'Notification process listener is not loopback-only'}
            }
            $checks.Add('alertmanager-resolved-webhook-delivered-loopback-only')
        }
        $process.StandardInput.WriteLine('shutdown')
        if(-not $process.WaitForExit(12000) -or $process.ExitCode -ne 0){throw 'Recovered target shutdown failed'}
        $process.Dispose();$process=$null
    }
    $peerResponse=Request '/metrics' "Bearer $peerToken" 'GET' $PeerPort
    try {
        $peerBody=$peerResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if([int]$peerResponse.StatusCode -ne 200 -or (Counter $peerBody 'et_process_id') -ne $peer.Id){throw 'Primary shutdown disrupted peer scrape'}
    }finally{$peerResponse.Dispose()}
    $peer.StandardInput.WriteLine('shutdown')
    if(-not $peer.WaitForExit(12000) -or $peer.ExitCode -ne 0){throw 'Peer shutdown failed'}
    $peer.Dispose();$peer=$null
    if(Get-NetTCPConnection -LocalPort $PeerPort -State Listen -ErrorAction SilentlyContinue){throw 'Peer exporter port retained'}
    $checks.Add('one-process-stop-does-not-disrupt-peer-scrape')
    foreach($badPort in @('', '0', '65536')){
        $process=Start-TestProcess $badPort $tokenFile
        if(-not $process.WaitForExit(12000) -or $process.ExitCode -ne 3){throw 'Invalid exporter configuration did not fail closed'}
        $process.Dispose();$process=$null
    }
    $checks.Add('invalid-configuration-exit-3')
    $process=Start-TestProcess '' ''
    $ready=$false
    for($attempt=0;$attempt -lt 100;$attempt++){
        if($process.HasExited){throw 'Default-disabled process exited unexpectedly'}
        try {
            $health=$client.GetAsync('http://127.0.0.1:30300/health/live').GetAwaiter().GetResult()
            $ready=$health.IsSuccessStatusCode;$health.Dispose()
        }catch { }
        if($ready){break}
        Start-Sleep -Milliseconds 100
    }
    if(-not $ready -or (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)){throw 'Default-disabled exporter behavior incorrect'}
    $process.StandardInput.WriteLine('shutdown')
    if(-not $process.WaitForExit(12000) -or $process.ExitCode -ne 0){throw 'Default-disabled shutdown failed'}
    $process.Dispose();$process=$null
    $checks.Add('disabled-by-default')
    [ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;Checks=@($checks);LocalCollectorVerified=[bool]$Prometheus;LocalNotificationVerified=[bool]$Alertmanager;LocalNotificationRetryVerified=[bool]$NotificationRetry;QueryEvidence=@($queryEvidence);ProductionMonitoringVerified=$false}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $fixture 'result.json')
    foreach($check in $checks){Write-Host "PASS $check"}
}finally{
    if($process){if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
    if($peer){if(-not $peer.HasExited){$peer.Kill($true);$peer.WaitForExit()};$peer.Dispose()}
    if($collector){if(-not $collector.HasExited){$collector.Kill($true);$collector.WaitForExit()};$collector.Dispose()}
    foreach($aux in @($manager,$receiver)){if($aux){if(-not $aux.HasExited){$aux.Kill($true);$aux.WaitForExit()};$aux.Dispose()}}
    $client.Dispose()
    if(Test-Path -LiteralPath $tokenFile){Remove-Item -LiteralPath $tokenFile -ErrorAction Stop}
    if(Test-Path -LiteralPath $peerTokenFile){Remove-Item -LiteralPath $peerTokenFile -ErrorAction Stop}
    if(Test-Path -LiteralPath $alertTokenFile){Remove-Item -LiteralPath $alertTokenFile -ErrorAction Stop}
}

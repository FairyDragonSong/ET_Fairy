param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[ValidateRange(1024,65535)][int]$Port=30491)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
foreach($testPort in @($Port,30300)){
    if(Get-NetTCPConnection -LocalPort $testPort -State Listen -ErrorAction SilentlyContinue){throw "Test port occupied: $testPort"}
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
$handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false
$client=[Net.Http.HttpClient]::new($handler)
$client.Timeout=[TimeSpan]::FromSeconds(3)
$checks=[Collections.Generic.List[string]]::new()
function Start-TestProcess([string]$ConfiguredPort,[string]$CredentialFile){
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
    $info.WorkingDirectory="$repo/Bin"
    $info.Arguments='--Process=8 --StartConfig=StartConfig/Release --Console=1 --ShutdownTimeoutSeconds=5'
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
function Request([string]$Path,[string]$Authorization,[string]$Method='GET'){
    $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),"http://127.0.0.1:$Port$Path")
    try {
        if($Authorization){$request.Headers.TryAddWithoutValidation('Authorization',$Authorization)|Out-Null}
        return $client.SendAsync($request).GetAwaiter().GetResult()
    } finally {$request.Dispose()}
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
    [ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Passed=$true;Checks=@($checks);ProductionMonitoringVerified=$false}|ConvertTo-Json|Set-Content (Join-Path $fixture 'result.json')
    foreach($check in $checks){Write-Host "PASS $check"}
}finally{
    if($process){if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
    $client.Dispose()
    if(Test-Path -LiteralPath $tokenFile){Remove-Item -LiteralPath $tokenFile -ErrorAction Stop}
}

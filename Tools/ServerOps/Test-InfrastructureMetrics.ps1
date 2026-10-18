param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
& "$PSScriptRoot/Install-LocalInfrastructureExporters.ps1" -EnvironmentRoot $EnvironmentRoot
. "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot

$promRoot=Join-Path $EnvironmentRoot 'prometheus-3.13.3\prometheus-3.13.3.windows-amd64'
$windowsBinary=Join-Path $EnvironmentRoot 'windows-exporter-0.31.7\windows_exporter.exe'
$mongoBinary=Join-Path $EnvironmentRoot 'mongodb-exporter-0.47.0\mongodb_exporter-0.47.0.windows-amd64\mongodb_exporter.exe'
foreach($path in @("$promRoot\prometheus.exe","$promRoot\promtool.exe",$windowsBinary,$mongoBinary)){if(-not(Test-Path -LiteralPath $path)){throw "Required binary missing: $path"}}

$ports=@(30912,30913,30914)
$listeners=@((Get-NetTCPConnection -State Listen).LocalPort)
foreach($port in $ports){if($port -in $listeners){throw "Test port occupied: $port"}}
$fixture=Join-Path ([IO.Path]::GetFullPath($EnvironmentRoot)) ('infrastructure-metrics-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $fixture /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F'|Out-Null
if($LASTEXITCODE -ne 0){throw 'Cannot protect infrastructure metrics fixture'}

$env:ET_METRICS_TEST_USER='et_metrics_'+[Guid]::NewGuid().ToString('N')
$env:ET_METRICS_TEST_PASSWORD=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ET_METRICS_TEST_CLEANUP='0'
$roleProvisionAttempted=$false;$passed=$false
$loopbackOnly=$false;$credentialsAbsentFromCommandLine=$false
$windows=$null;$mongo=$null;$prometheus=$null
$windowsStarted=$false;$mongoStarted=$false;$prometheusStarted=$false
$queries=[Collections.Generic.List[object]]::new()
$diagnostics=[Collections.Generic.List[object]]::new()
$mongoMetricAllowlist='^(mongodb_up|mongodb_rs_members_(health|state|optimeDate)|mongodb_ss_(connections|opcounters|wt_cache_(bytes_currently_in_the_cache|maximum_bytes_configured)))$'
# 启动并跟踪由当前测试负责回收的进程。
function Start-OwnedProcess([string]$File,[string[]]$Arguments,[hashtable]$Environment){
    $info=[Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    foreach($argument in $Arguments){$info.ArgumentList.Add($argument)}
    foreach($entry in $Environment.GetEnumerator()){$info.Environment[$entry.Key]=[string]$entry.Value}
    $child=[Diagnostics.Process]::new();$child.StartInfo=$info
    if(-not $child.Start()){throw "Process start failed: $File"}
    [pscustomobject]@{Process=$child;Output=$child.StandardOutput.ReadToEndAsync();Error=$child.StandardError.ReadToEndAsync()}
}
# 轮询查询，直到结果满足预期或超时。
function Wait-Query([string]$Expression,[scriptblock]$Accept){
    for($attempt=0;$attempt -lt 40;$attempt++){
        foreach($child in @($windows,$mongo,$prometheus)){if($child -and $child.Process.HasExited){throw "Metrics child exited: $($child.Process.StartInfo.FileName): stdout=$($child.Output.Result) stderr=$($child.Error.Result)"}}
        try{
            $reply=Invoke-RestMethod -NoProxy -TimeoutSec 3 -Uri ('http://127.0.0.1:30914/api/v1/query?query='+[Uri]::EscapeDataString($Expression))
            $values=@($reply.data.result)
            if($reply.status -eq 'success' -and (& $Accept $values)){
                $queries.Add([ordered]@{Expression=$Expression;Series=$values.Count;ObservedAtUtc=[DateTime]::UtcNow.ToString('O')})
                return
            }
        }catch{}
        Start-Sleep -Milliseconds 500
    }
    throw "Prometheus query did not satisfy gate: $Expression"
}
try{
    $roleProvisionAttempted=$true
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalInfrastructureMetricsRole.js"
    if($LASTEXITCODE -ne 0){throw 'Metrics role provisioning failed'}

    $windows=Start-OwnedProcess $windowsBinary @('--web.listen-address=127.0.0.1:30912','--collectors.enabled=cpu,memory,logical_disk,net,os,system') @{}
    $windowsStarted=$true
    $mongoUri='mongodb://127.0.0.1:27101/admin?authSource=admin&tls=true&directConnection=true&retryWrites=false&tlsCAFile='+[Uri]::EscapeDataString($env:ET_MONGO_CA_FILE)
    $mongo=Start-OwnedProcess $mongoBinary @('--web.listen-address=127.0.0.1:30913','--mongodb.direct-connect','--collector.diagnosticdata','--collector.replicasetstatus') @{
        MONGODB_URI=$mongoUri;MONGODB_USER=$env:ET_METRICS_TEST_USER;MONGODB_PASSWORD=$env:ET_METRICS_TEST_PASSWORD
    }
    $mongoStarted=$true

    $config=Join-Path $fixture 'prometheus.json'
    @{global=@{scrape_interval='1s';scrape_timeout='800ms'};scrape_configs=@(
        @{job_name='windows-host';static_configs=@(@{targets=@('127.0.0.1:30912')})},
        @{job_name='mongodb';scrape_interval='15s';scrape_timeout='10s';static_configs=@(@{targets=@('127.0.0.1:30913')});
            metric_relabel_configs=@(@{source_labels=@('__name__');regex=$mongoMetricAllowlist;action='keep'})}
    )}|ConvertTo-Json -Depth 8|Set-Content $config
    & "$promRoot\promtool.exe" check config $config
    if($LASTEXITCODE -ne 0){throw 'Infrastructure Prometheus config invalid'}
    $prometheus=Start-OwnedProcess "$promRoot\prometheus.exe" @("--config.file=$config",'--web.listen-address=127.0.0.1:30914',"--storage.tsdb.path=$fixture\tsdb",'--storage.tsdb.retention.time=1d','--storage.tsdb.retention.size=64MB') @{}
    $prometheusStarted=$true

    Wait-Query 'up{job="windows-host"}' {param($v)$v.Count -eq 1 -and $v[0].value[1] -eq '1'}
    Wait-Query 'up{job="mongodb"}' {param($v)$v.Count -eq 1 -and $v[0].value[1] -eq '1'}
    Wait-Query 'mongodb_up{job="mongodb"}' {param($v)$v.Count -eq 1 -and $v[0].value[1] -eq '1'}
    $mongoText=(Invoke-WebRequest -UseBasicParsing -NoProxy http://127.0.0.1:30913/metrics -TimeoutSec 10).Content
    $mongoNames=@($mongoText -split "`n"|Where-Object {$_ -match '^(mongodb_[a-zA-Z0-9_:]+)' }|ForEach-Object {$Matches[1]}|Sort-Object -Unique)
    $mongoOperationalNames=@($mongoNames|Where-Object {$_ -match $mongoMetricAllowlist})
    foreach($requiredName in @('mongodb_up','mongodb_rs_members_health','mongodb_rs_members_state','mongodb_rs_members_optimeDate','mongodb_ss_connections','mongodb_ss_opcounters','mongodb_ss_wt_cache_bytes_currently_in_the_cache','mongodb_ss_wt_cache_maximum_bytes_configured')){
        if($requiredName -notin $mongoOperationalNames){throw "Required MongoDB metric missing: $requiredName"}
    }
    Wait-Query 'count({job="windows-host",__name__=~"windows_(cpu|memory|logical_disk|net|os|system)_.*"})' {param($v)$v.Count -eq 1 -and [double]$v[0].value[1] -gt 10}
    Wait-Query 'count({job="mongodb",__name__=~"mongodb_.*"})' {param($v)$v.Count -eq 1 -and [double]$v[0].value[1] -ge 10 -and [double]$v[0].value[1] -le 100}
    Wait-Query 'count(mongodb_rs_members_health{job="mongodb"} == 1)' {param($v)$v.Count -eq 1 -and $v[0].value[1] -eq '3'}
    Wait-Query 'count(mongodb_rs_members_state{job="mongodb"})' {param($v)$v.Count -eq 1 -and $v[0].value[1] -eq '3'}
    Wait-Query 'mongodb_ss_connections{job="mongodb",conn_type="current"}' {param($v)$v.Count -eq 1 -and [double]$v[0].value[1] -gt 0}
    Wait-Query 'count(mongodb_ss_opcounters{job="mongodb"})' {param($v)$v.Count -eq 1 -and [double]$v[0].value[1] -ge 6}
    Wait-Query 'mongodb_ss_wt_cache_maximum_bytes_configured{job="mongodb"}' {param($v)$v.Count -eq 1 -and [double]$v[0].value[1] -gt 0}

    foreach($entry in @(@{Port=30912;Pid=$windows.Process.Id},@{Port=30913;Pid=$mongo.Process.Id},@{Port=30914;Pid=$prometheus.Process.Id})){
        $listener=Get-NetTCPConnection -State Listen -LocalPort $entry.Port|Where-Object OwningProcess -eq $entry.Pid
        if(-not $listener -or @($listener|Where-Object {$_.LocalAddress -notin @('127.0.0.1','::1')}).Count){throw "Non-loopback or missing listener: $($entry.Port)"}
    }
    $loopbackOnly=$true
    $commandLine=(Get-CimInstance Win32_Process -Filter "ProcessId=$($mongo.Process.Id)").CommandLine
    if($commandLine.Contains($env:ET_METRICS_TEST_USER) -or $commandLine.Contains($env:ET_METRICS_TEST_PASSWORD)){throw 'MongoDB exporter command line disclosed credentials'}
    $credentialsAbsentFromCommandLine=$true

    $windowsText=(Invoke-WebRequest -UseBasicParsing -NoProxy http://127.0.0.1:30912/metrics -TimeoutSec 10).Content
    $windowsNames=@($windowsText -split "`n"|Where-Object {$_ -match '^(windows_[a-zA-Z0-9_:]+)' }|ForEach-Object {$Matches[1]}|Sort-Object -Unique)
    $passed=$true
    Write-Host "PASS infrastructure metrics windows=$($windowsNames.Count) mongodb_raw=$($mongoNames.Count) mongodb_allowlisted=$($mongoOperationalNames.Count) loopback-only credentials-not-in-command-line"
}finally{
    foreach($child in @($prometheus,$mongo,$windows)){
        if($child){
            try{
                if(-not $child.Process.HasExited){$child.Process.Kill($true);$child.Process.WaitForExit()}
                $stderr=[string]$child.Error.Result
                $stdout=[string]$child.Output.Result
                if($env:ET_METRICS_TEST_PASSWORD){$stderr=$stderr.Replace($env:ET_METRICS_TEST_PASSWORD,'[REDACTED]')}
                if($env:ET_METRICS_TEST_PASSWORD){$stdout=$stdout.Replace($env:ET_METRICS_TEST_PASSWORD,'[REDACTED]')}
                if($stderr.Length -gt 12000){$stderr=$stderr.Substring($stderr.Length-12000)}
                if($stdout.Length -gt 12000){$stdout=$stdout.Substring($stdout.Length-12000)}
                $diagnostics.Add([ordered]@{File=[IO.Path]::GetFileName($child.Process.StartInfo.FileName);ExitCode=$child.Process.ExitCode;StandardOutput=$stdout;StandardError=$stderr})
            }finally{$child.Process.Dispose()}
        }
    }
    $roleRemoved=-not $roleProvisionAttempted
    if($roleProvisionAttempted){
        $env:ET_METRICS_TEST_CLEANUP='1'
        try{
            & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalInfrastructureMetricsRole.js"
            $roleRemoved=$LASTEXITCODE -eq 0
        }catch{$roleRemoved=$false}
    }
    $report=[ordered]@{Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);Passed=$passed;CompletedAtUtc=[DateTime]::UtcNow.ToString('O');
        Exporters=[ordered]@{Windows='0.31.7';MongoDB='0.47.0'};Queries=@($queries);LoopbackOnly=$loopbackOnly;CredentialsAbsentFromCommandLine=$credentialsAbsentFromCommandLine;
        MongoDBRawMetricFamilies=$(if($mongoNames){$mongoNames.Count}else{0});MongoDBAllowlistedMetricFamilies=$(if($mongoOperationalNames){$mongoOperationalNames.Count}else{0});
        TemporaryRoleRemoved=$roleRemoved;Diagnostics=@($diagnostics);ProductionDeploymentVerified=$false;CrossHostVerified=$false}
    $report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $fixture 'result.json')
    if(-not $passed){$diagnostics|ForEach-Object {if($_.StandardOutput){Write-Host "$($_.File) stdout: $($_.StandardOutput)"};if($_.StandardError){Write-Host "$($_.File) stderr: $($_.StandardError)"}}}
    $env:ET_METRICS_TEST_USER=$null;$env:ET_METRICS_TEST_PASSWORD=$null;$env:ET_METRICS_TEST_CLEANUP=$null
    Write-Host "Evidence: $(Join-Path $fixture 'result.json')"
    if($passed -and -not $roleRemoved){throw 'Temporary metrics role cleanup failed'}
}

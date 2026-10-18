param(
    [string]$PrometheusPath = 'D:\Program Files\ET\_Env\prometheus-3.13.3\prometheus-3.13.3.windows-amd64\prometheus.exe',
    [string]$PromtoolPath = 'D:\Program Files\ET\_Env\prometheus-3.13.3\prometheus-3.13.3.windows-amd64\promtool.exe',
    [switch]$MetricsSource,
    [int]$SourcePort,
    [string]$SourceReadyFile,
    [string]$SourceReceiptFile
)

$ErrorActionPreference = 'Stop'

if ($MetricsSource) {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$SourcePort)
    try {
        $listener.Start();[IO.File]::WriteAllText($SourceReadyFile,'ready')
        while($true){
            $client=$listener.AcceptTcpClient()
            try {
                $stream=$client.GetStream();$bytes=[Collections.Generic.List[byte]]::new()
                while($bytes.Count-lt 65536){
                    $value=$stream.ReadByte();if($value-lt 0){break};$bytes.Add([byte]$value);$count=$bytes.Count
                    if($count-ge 4-and$bytes[$count-4]-eq 13-and$bytes[$count-3]-eq 10-and$bytes[$count-2]-eq 13-and$bytes[$count-1]-eq 10){break}
                }
                $headers=[Text.Encoding]::ASCII.GetString($bytes.ToArray())
                $authorized=$headers-match("(?im)^Authorization:\s*Bearer\s+"+[regex]::Escape($env:ET_METRICS_TEST_TOKEN)+"\s*$")
                if($authorized){
                    $payload=[Text.Encoding]::UTF8.GetBytes("# TYPE et_relay_transport_value gauge`net_relay_transport_value 1`n")
                    [IO.File]::AppendAllText($SourceReceiptFile,[DateTime]::UtcNow.ToString('O')+[Environment]::NewLine)
                    $response=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Type: text/plain; version=0.0.4`r`nContent-Length: $($payload.Length)`r`nConnection: close`r`n`r`n")
                }else{
                    $payload=[Text.Encoding]::UTF8.GetBytes('unauthorized')
                    $response=[Text.Encoding]::ASCII.GetBytes("HTTP/1.1 401 Unauthorized`r`nContent-Length: $($payload.Length)`r`nConnection: close`r`n`r`n")
                }
                $stream.Write($response,0,$response.Length);$stream.Write($payload,0,$payload.Length);$stream.Flush()
            }finally{$client.Dispose()}
        }
    }finally{$listener.Stop()}
}

$prometheus=[IO.Path]::GetFullPath($PrometheusPath);$promtool=[IO.Path]::GetFullPath($PromtoolPath)
foreach($entry in @(@($prometheus,'Pinned Prometheus'),@($promtool,'Pinned promtool'))){if(-not(Test-Path -LiteralPath $entry[0] -PathType Leaf)){throw "$($entry[1]) is not installed"}}
$address=[Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName())|Where-Object{$_.AddressFamily-eq[Net.Sockets.AddressFamily]::InterNetwork-and-not[Net.IPAddress]::IsLoopback($_)}|Select-Object -First 1
if($null-eq$address){throw 'A non-loopback IPv4 address is required for the transport drill'}
# 申请一个当前可用的本地端口。
function Get-FreePort([Net.IPAddress]$Address){$probe=[Net.Sockets.TcpListener]::new($Address,0);$probe.Start();try{return([Net.IPEndPoint]$probe.LocalEndpoint).Port}finally{$probe.Stop()}}
$sourcePort=Get-FreePort ([Net.IPAddress]::Loopback);$webPort=Get-FreePort ([Net.IPAddress]::Loopback);$remotePort=Get-FreePort $address
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-metrics-transport-'+[guid]::NewGuid().ToString('N'))
$ready=Join-Path $fixture 'source.ready';$sourceReceipts=Join-Path $fixture 'source.receipts';$receiverReady=Join-Path $fixture 'receiver.ready';$remoteReceipts=Join-Path $fixture 'remote.jsonl'
$sourceProcess=$null;$prometheusProcess=$null;$receiverProcess=$null
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
New-Item -ItemType Directory -Path $fixture|Out-Null
try{
    $rootKey=[Security.Cryptography.RSA]::Create(2048)
    $rootRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Local Metrics Test Root',$rootKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true));$rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey,$false))
    $rootCertificate=$rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(7))
    $leafKey=[Security.Cryptography.RSA]::Create(2048)
    $leafRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Local Metrics Receiver',$leafKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new();$san.AddIpAddress($address);$leafRequest.CertificateExtensions.Add($san.Build());$leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true));$leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
    $oids=[Security.Cryptography.OidCollection]::new();$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))|Out-Null;$leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
    $serial=[Security.Cryptography.RandomNumberGenerator]::GetBytes(16);$serial[0]=$serial[0]-band 0x7f
    $issued=$leafRequest.Create($rootCertificate,[DateTimeOffset]::UtcNow.AddHours(-1),[DateTimeOffset]::UtcNow.AddDays(2),$serial)
    $leafCertificate=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$leafKey)
    $pfxPassword=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24));$pfx=Join-Path $fixture 'receiver.pfx';$ca=Join-Path $fixture 'root.pem'
    [IO.File]::WriteAllBytes($pfx,$leafCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$pfxPassword));[IO.File]::WriteAllText($ca,$rootCertificate.ExportCertificatePem(),[Text.UTF8Encoding]::new($false))
    $scrapeTokenValue=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32));$scrapeToken=Join-Path $fixture 'scrape.token';[IO.File]::WriteAllText($scrapeToken,$scrapeTokenValue)
    $writeToken=Join-Path $fixture 'write.token';[IO.File]::WriteAllText($writeToken,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $config=Join-Path $fixture 'prometheus.json'
    & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" -HostId local-host -Deployment transport-test `
        -RemoteWriteUrl "https://$($address):$remotePort/api/v1/write" -RemoteWriteTokenFile $writeToken `
        -OutputFile $config -EtMetricsPort $sourcePort -EtMetricsTokenFile $scrapeToken -CertificateAuthorityFile $ca|Out-Null
    $parsed=Get-Content -LiteralPath $config -Raw|ConvertFrom-Json -Depth 30;$parsed.global.scrape_interval='1s';$parsed.global.scrape_timeout='500ms'
    [IO.File]::WriteAllText($config,($parsed|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
    & $promtool check config $config|Out-Null;if($LASTEXITCODE-ne0){throw 'Initial Prometheus transport config is invalid'}

    $sourceInfo=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path);$sourceInfo.UseShellExecute=$false;$sourceInfo.CreateNoWindow=$true;$sourceInfo.RedirectStandardOutput=$true;$sourceInfo.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-File',$PSCommandPath,'-MetricsSource','-SourcePort',[string]$sourcePort,'-SourceReadyFile',$ready,'-SourceReceiptFile',$sourceReceipts)){$sourceInfo.ArgumentList.Add($argument)};$sourceInfo.Environment['ET_METRICS_TEST_TOKEN']=$scrapeTokenValue
    $sourceProcess=[Diagnostics.Process]::new();$sourceProcess.StartInfo=$sourceInfo;if(-not$sourceProcess.Start()){throw 'Metrics source start failed'};$ownedProcesses.Add($sourceProcess);$sourceOutput=$sourceProcess.StandardOutput.ReadToEndAsync();$sourceError=$sourceProcess.StandardError.ReadToEndAsync()
    for($i=0;$i-lt50-and-not(Test-Path -LiteralPath $ready);$i++){Start-Sleep -Milliseconds 100};if(-not(Test-Path -LiteralPath $ready)){throw 'Metrics source did not become ready'}
    $storage=Join-Path $fixture 'tsdb'
    function Start-Prometheus([string]$Configuration){$info=[Diagnostics.ProcessStartInfo]::new($prometheus);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;foreach($argument in @("--config.file=$Configuration","--storage.tsdb.path=$storage",'--storage.tsdb.retention.time=1d','--storage.tsdb.retention.size=64MB',"--web.listen-address=127.0.0.1:$webPort")){$info.ArgumentList.Add($argument)};$process=[Diagnostics.Process]::new();$process.StartInfo=$info;if(-not$process.Start()){throw 'Prometheus start failed'};$ownedProcesses.Add($process);return $process}
    $prometheusProcess=Start-Prometheus $config;$promOutput=$prometheusProcess.StandardOutput.ReadToEndAsync();$promError=$prometheusProcess.StandardError.ReadToEndAsync()
    for($i=0;$i-lt100-and-not(Test-Path -LiteralPath $sourceReceipts);$i++){if($prometheusProcess.HasExited){throw "Prometheus exited before scrape: $($prometheusProcess.ExitCode)"};Start-Sleep -Milliseconds 100}
    if(-not(Test-Path -LiteralPath $sourceReceipts)){throw 'Prometheus did not scrape the authenticated local target'}
    Start-Sleep -Seconds 8
    if($prometheusProcess.HasExited){throw "Prometheus exited during center outage: $($prometheusProcess.ExitCode)"}
    if(@(Get-Content -LiteralPath $sourceReceipts).Count-lt2){throw 'Authenticated local scraping did not continue while the center was offline'}
    if(-not(Test-Path -LiteralPath (Join-Path $storage 'wal'))-or@(Get-ChildItem -LiteralPath (Join-Path $storage 'wal') -File -Recurse).Count-eq0){throw 'Prometheus WAL was not created during center outage'}

    $receiverInfo=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path);$receiverInfo.UseShellExecute=$false;$receiverInfo.CreateNoWindow=$true;$receiverInfo.RedirectStandardOutput=$true;$receiverInfo.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-File',"$PSScriptRoot/Test-ProductionLogForwarderTransport.ps1",'-Receiver','-ListenAddress',$address.ToString(),'-ListenPort',[string]$remotePort,'-CertificateFile',$pfx,'-ReadyFile',$receiverReady,'-ReceiptFile',$remoteReceipts)){$receiverInfo.ArgumentList.Add($argument)};$receiverInfo.Environment['ET_OTLP_TEST_PFX_PASSWORD']=$pfxPassword
    $receiverProcess=[Diagnostics.Process]::new();$receiverProcess.StartInfo=$receiverInfo;if(-not$receiverProcess.Start()){throw 'TLS remote write receiver start failed'};$ownedProcesses.Add($receiverProcess);$receiverOutput=$receiverProcess.StandardOutput.ReadToEndAsync();$receiverError=$receiverProcess.StandardError.ReadToEndAsync()
    for($i=0;$i-lt50-and-not(Test-Path -LiteralPath $receiverReady);$i++){Start-Sleep -Milliseconds 100};if(-not(Test-Path -LiteralPath $receiverReady)){throw 'TLS remote write receiver did not become ready'}
    if(-not$receiverProcess.WaitForExit(30000)){
        if(-not$receiverProcess.HasExited){$receiverProcess.Kill($true);$receiverProcess.WaitForExit()}
        $received=$(if(Test-Path -LiteralPath $remoteReceipts){@(Get-Content -LiteralPath $remoteReceipts).Count}else{0})
        throw "Prometheus did not retry pending remote write; received=$received"
    };if($receiverProcess.ExitCode-ne0){throw "TLS receiver failed: $($receiverError.Result)"}
    $events=@(Get-Content -LiteralPath $remoteReceipts|ForEach-Object{$_|ConvertFrom-Json})
    if($events.Count-ne2-or$events[0].Status-ne503-or$events[1].Status-ne200-or$events.Path-notcontains'/api/v1/write'-or$events.AuthorizationPresent-contains$false-or$events[0].BodySha256-cne$events[1].BodySha256-or$events.ContentEncoding-notcontains'snappy') {throw 'Prometheus WAL retry evidence is incomplete'}
    if(-not$prometheusProcess.HasExited){$prometheusProcess.Kill($true);$prometheusProcess.WaitForExit()}
    [ordered]@{Passed=$true;Prometheus=(& $prometheus --version 2>&1|Select-Object -First 1);Tls=$true;NonLoopbackAddress=$address.ToString();AuthenticatedLocalScrape=$true;CenterOfflineObserved=$true;WalObserved=$true;PrometheusRestarted=$false;FirstResponse=503;RetryResponse=200;RetryBodyMatched=$true;Snappy=$true;AuthorizationPresent=$true;CrashReplayGuaranteed=$false;CrossHostVerified=$false}|ConvertTo-Json
}finally{
    foreach($process in $ownedProcesses){try{if(-not$process.HasExited){$process.Kill($true);$process.WaitForExit()}}finally{$process.Dispose()}}
    foreach($value in @($leafCertificate,$issued,$rootCertificate,$leafKey,$rootKey)){if($null-ne$value){$value.Dispose()}}
    Remove-Item Env:ET_METRICS_TEST_TOKEN,Env:ET_OTLP_TEST_PFX_PASSWORD -ErrorAction SilentlyContinue
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar;$resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-metrics-transport-[a-f0-9]{32}$'-and(Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

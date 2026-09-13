param(
    [string]$CollectorPath = 'D:\Program Files\ET\_Env\otelcol-contrib-0.153.0\otelcol-contrib.exe',
    [switch]$Receiver,
    [string]$ListenAddress,
    [int]$ListenPort,
    [string]$CertificateFile,
    [string]$ReadyFile,
    [string]$ReceiptFile
)

$ErrorActionPreference = 'Stop'

if ($Receiver) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $CertificateFile,$env:ET_OTLP_TEST_PFX_PASSWORD,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($ListenAddress),$ListenPort)
    try {
        $listener.Start()
        [IO.File]::WriteAllText($ReadyFile,'ready')
        for ($requestIndex=0;$requestIndex -lt 2;$requestIndex++) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = [Net.Security.SslStream]::new($client.GetStream(),$false)
                try {
                    $stream.AuthenticateAsServer($certificate,$false,
                        [Security.Authentication.SslProtocols]::Tls12 -bor [Security.Authentication.SslProtocols]::Tls13,$false)
                    $headerBytes = [Collections.Generic.List[byte]]::new()
                    while ($headerBytes.Count -lt 65536) {
                        $value = $stream.ReadByte()
                        if ($value -lt 0) { throw 'TLS client closed before HTTP headers completed' }
                        $headerBytes.Add([byte]$value)
                        $count = $headerBytes.Count
                        if ($count -ge 4 -and $headerBytes[$count-4] -eq 13 -and $headerBytes[$count-3] -eq 10 -and
                            $headerBytes[$count-2] -eq 13 -and $headerBytes[$count-1] -eq 10) { break }
                    }
                    $headers = [Text.Encoding]::ASCII.GetString($headerBytes.ToArray())
                    if ($headers -notmatch '^(POST) ([^ ]+) HTTP/1\.[01]' -or $headers -notmatch '(?im)^Content-Length:\s*(\d+)\s*$') {
                        throw 'Unsupported OTLP HTTP request framing'
                    }
                    $path = [regex]::Match($headers,'^POST ([^ ]+) HTTP/1\.[01]').Groups[1].Value
                    $length = [int][regex]::Match($headers,'(?im)^Content-Length:\s*(\d+)\s*$').Groups[1].Value
                    if ($length -le 0 -or $length -gt 2MB) { throw 'OTLP request body size is invalid' }
                    $body = [byte[]]::new($length)
                    $offset = 0
                    while ($offset -lt $length) {
                        $read = $stream.Read($body,$offset,$length-$offset)
                        if ($read -le 0) { throw 'TLS client closed before OTLP body completed' }
                        $offset += $read
                    }
                    $contentType = [regex]::Match($headers,'(?im)^Content-Type:\s*([^\r\n]+)').Groups[1].Value.Trim()
                    $contentEncoding = [regex]::Match($headers,'(?im)^Content-Encoding:\s*([^\r\n]+)').Groups[1].Value.Trim()
                    $authorized = $headers -match '(?im)^Authorization:\s*Bearer\s+\S+\s*$'
                    $receipt = [ordered]@{
                        Attempt=$requestIndex+1;Path=$path;ContentType=$contentType;ContentEncoding=$contentEncoding;Bytes=$length
                        BodySha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($body)).ToLowerInvariant()
                        AuthorizationPresent=[bool]$authorized;ReceivedAtUtc=[DateTime]::UtcNow.ToString('O')
                        Status=$(if($requestIndex -eq 0){503}else{200})
                    } | ConvertTo-Json -Compress
                    [IO.File]::AppendAllText($ReceiptFile,$receipt+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
                    $status = if ($requestIndex -eq 0) {'503 Service Unavailable'} else {'200 OK'}
                    $payload = [Text.Encoding]::UTF8.GetBytes('{}')
                    $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $status`r`nContent-Type: application/json`r`nContent-Length: 2`r`nConnection: close`r`n`r`n")
                    $stream.Write($response,0,$response.Length);$stream.Write($payload,0,$payload.Length);$stream.Flush()
                }
                finally { $stream.Dispose() }
            }
            finally { $client.Dispose() }
        }
    }
    finally { $listener.Stop();$certificate.Dispose() }
    exit 0
}

$collector = [IO.Path]::GetFullPath($CollectorPath)
if (-not (Test-Path -LiteralPath $collector -PathType Leaf)) { throw 'Pinned OpenTelemetry Collector is not installed' }
$address = [Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) |
    Where-Object { $_.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_) } |
    Select-Object -First 1
if ($null -eq $address) { throw 'A non-loopback IPv4 address is required for the transport drill' }
$portProbe = [Net.Sockets.TcpListener]::new($address,0);$portProbe.Start();$port=([Net.IPEndPoint]$portProbe.LocalEndpoint).Port;$portProbe.Stop()
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('et-log-transport-'+[guid]::NewGuid().ToString('N'))
$inputRoot = Join-Path $fixture 'logs';$state=Join-Path $fixture 'state'
$ready=Join-Path $fixture 'receiver.ready';$receipts=Join-Path $fixture 'receipts.jsonl'
$collectorProcess=$null;$receiverProcess=$null
New-Item -ItemType Directory -Path (Join-Path $inputRoot '101') | Out-Null
try {
    $rootKey=[Security.Cryptography.RSA]::Create(2048)
    $rootRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Local OTLP Test Root',$rootKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $rootRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($rootRequest.PublicKey,$false))
    $rootCertificate=$rootRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1),[DateTimeOffset]::UtcNow.AddDays(7))
    $leafKey=[Security.Cryptography.RSA]::Create(2048)
    $leafRequest=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Local OTLP Receiver',$leafKey,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $san=[Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new();$san.AddIpAddress($address)
    $leafRequest.CertificateExtensions.Add($san.Build())
    $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
    $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
    $oids=[Security.Cryptography.OidCollection]::new();$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))|Out-Null
    $leafRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids,$true))
    $serial=[Security.Cryptography.RandomNumberGenerator]::GetBytes(16);$serial[0]=$serial[0] -band 0x7f
    $issued=$leafRequest.Create($rootCertificate,[DateTimeOffset]::UtcNow.AddHours(-1),[DateTimeOffset]::UtcNow.AddDays(2),$serial)
    $leafCertificate=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued,$leafKey)
    $pfxPassword=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    $pfx=Join-Path $fixture 'receiver.pfx';$ca=Join-Path $fixture 'root.pem'
    [IO.File]::WriteAllBytes($pfx,$leafCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$pfxPassword))
    [IO.File]::WriteAllText($ca,$rootCertificate.ExportCertificatePem(),[Text.UTF8Encoding]::new($false))
    $token=Join-Path $fixture 'central.token';[IO.File]::WriteAllText($token,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $config=Join-Path $fixture 'collector.json'
    & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
        -OtlpHttpEndpoint "https://$($address):$port/otlp" -BearerTokenFile $token -ConfigurationFile $config `
        -CertificateAuthorityFile $ca -Deployment 'transport-test' -HostId 'local-host' -StartAt beginning | Out-Null
    $validation=& $collector validate "--config=file:$($config.Replace('\','/'))" 2>&1
    if($LASTEXITCODE -ne 0){throw "Collector validation failed: $($validation -join ' ')"}
    [IO.File]::WriteAllText((Join-Path $inputRoot '101\info.log'),"2026-09-13 00:00:00|Info|transport|correlation_id=transport-test persisted-after-outage`r`n")

    function Start-Collector {
        $info=[Diagnostics.ProcessStartInfo]::new($collector);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.ArgumentList.Add("--config=file:$($config.Replace('\','/'))")
        $process=[Diagnostics.Process]::new();$process.StartInfo=$info
        if(-not $process.Start()){throw 'Collector start failed'}
        return $process
    }
    $collectorProcess=Start-Collector
    $collectorOutput=$collectorProcess.StandardOutput.ReadToEndAsync();$collectorError=$collectorProcess.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 4
    if($collectorProcess.HasExited){throw "Collector exited during simulated outage: $($collectorProcess.ExitCode)"}
    $collectorProcess.Kill($true);if(-not $collectorProcess.WaitForExit(10000)){throw 'Collector crash was not observed'}
    $collectorProcess.Dispose();$collectorProcess=$null
    if(-not(Test-Path -LiteralPath $state)-or@(Get-ChildItem -LiteralPath $state -File -Recurse).Count -eq 0){throw 'Persistent Collector state was not created before crash'}

    $receiverInfo=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path);$receiverInfo.UseShellExecute=$false;$receiverInfo.CreateNoWindow=$true
    $receiverInfo.RedirectStandardOutput=$true;$receiverInfo.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-File',$PSCommandPath,'-Receiver','-ListenAddress',$address.ToString(),'-ListenPort',[string]$port,'-CertificateFile',$pfx,'-ReadyFile',$ready,'-ReceiptFile',$receipts)){$receiverInfo.ArgumentList.Add($argument)}
    $receiverInfo.Environment['ET_OTLP_TEST_PFX_PASSWORD']=$pfxPassword
    $receiverProcess=[Diagnostics.Process]::new();$receiverProcess.StartInfo=$receiverInfo
    if(-not $receiverProcess.Start()){throw 'TLS receiver start failed'}
    $receiverOutput=$receiverProcess.StandardOutput.ReadToEndAsync();$receiverError=$receiverProcess.StandardError.ReadToEndAsync()
    for($i=0;$i -lt 50 -and -not(Test-Path -LiteralPath $ready);$i++){Start-Sleep -Milliseconds 100}
    if(-not(Test-Path -LiteralPath $ready)){throw 'TLS receiver did not become ready'}

    $collectorProcess=Start-Collector
    $collectorOutput=$collectorProcess.StandardOutput.ReadToEndAsync();$collectorError=$collectorProcess.StandardError.ReadToEndAsync()
    if(-not $receiverProcess.WaitForExit(30000)){throw 'Collector did not retry the persisted OTLP request'}
    if($receiverProcess.ExitCode -ne 0){throw "TLS receiver failed: $($receiverError.Result)"}
    $events=@(Get-Content -LiteralPath $receipts|ForEach-Object{$_|ConvertFrom-Json})
    if($events.Count -ne 2 -or $events[0].Status -ne 503 -or $events[1].Status -ne 200 -or
        $events.Path -notcontains '/otlp/v1/logs' -or $events.AuthorizationPresent -contains $false -or
        $events[0].BodySha256 -cne $events[1].BodySha256 -or $events.Bytes -contains 0){
        throw 'Persisted OTLP retry evidence is incomplete'
    }
    if(-not $collectorProcess.HasExited){$collectorProcess.Kill($true);$collectorProcess.WaitForExit()}
    [ordered]@{
        Passed=$true;Collector=(& $collector --version 2>&1|Select-Object -First 1);Tls=$true
        NonLoopbackAddress=$address.ToString();CenterOfflineBeforeCrash=$true;CollectorRestarted=$true
        FirstResponse=503;RetryResponse=200;RetryBodyMatched=$true;AuthorizationPresent=$true
        PersistentStateObserved=$true;CrossHostVerified=$false
    } | ConvertTo-Json
}
finally {
    foreach($process in @($collectorProcess,$receiverProcess)){
        if($null-ne$process){try{if(-not$process.HasExited){$process.Kill($true);$process.WaitForExit()}}finally{$process.Dispose()}}
    }
    foreach($value in @($leafCertificate,$issued,$rootCertificate,$leafKey,$rootKey)){if($null-ne$value){$value.Dispose()}}
    Remove-Item Env:ET_OTLP_TEST_PFX_PASSWORD -ErrorAction SilentlyContinue
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-log-transport-[a-f0-9]{32}$'-and(Test-Path -LiteralPath $resolved)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

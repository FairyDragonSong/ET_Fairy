param(
    [string]$CollectorPath = 'D:\Program Files\ET\_Env\otelcol-contrib-0.153.0\otelcol-contrib.exe'
)

$ErrorActionPreference = 'Stop'
$collector = [IO.Path]::GetFullPath($CollectorPath)
if (-not (Test-Path -LiteralPath $collector -PathType Leaf)) { throw 'Pinned OpenTelemetry Collector is not installed' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('et-log-forwarder-'+[guid]::NewGuid().ToString('N'))
$inputRoot = Join-Path $fixture 'logs'
$state = Join-Path $fixture 'state'
New-Item -ItemType Directory -Path (Join-Path $inputRoot '101') | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $inputRoot '101\info.log'),'2026-09-13 00:00:00|Info|test|message')
    $token = Join-Path $fixture 'central.token'
    [IO.File]::WriteAllText($token,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $configuration = Join-Path $fixture 'collector.json'
    $parameters = @{
        InputRoot=$inputRoot;StateDirectory=$state;OtlpHttpEndpoint='https://logs.example.invalid/otlp'
        BearerTokenFile=$token;ConfigurationFile=$configuration;Deployment='prod-cn-1';HostId='game-01'
    }
    $report = ((& "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @parameters) -join [Environment]::NewLine) | ConvertFrom-Json
    $validation = & $collector validate "--config=file:$($configuration.Replace('\','/'))" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Collector validation failed: $($validation -join ' ')" }
    $parsed = Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json -Depth 30
    $raw = Get-Content -LiteralPath $configuration -Raw
    if (-not $report.Passed -or $report.CrossHostVerified -or $report.ListenerEnabled -or
        $parsed.exporters.'otlp_http/central'.endpoint -cne 'https://logs.example.invalid/otlp' -or
        $parsed.extensions.'bearertokenauth/central'.filename -cne $token.Replace('\','/') -or
        -not $parsed.exporters.'otlp_http/central'.sending_queue.enabled -or
        $parsed.exporters.'otlp_http/central'.sending_queue.storage -cne 'file_storage' -or
        $parsed.exporters.'otlp_http/central'.retry_on_failure.max_elapsed_time -cne '0' -or
        $parsed.receivers.'file_log/et'.start_at -cne 'end' -or
        $raw.Contains([IO.File]::ReadAllText($token))) {
        throw 'Generated log forwarder configuration violated the production contract'
    }

    function Expect-Rejection([scriptblock]$Action,[string]$Message) {
        $rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $Message}
    }
    $base = $parameters.Clone();$base.ConfigurationFile=Join-Path $fixture 'unsafe.json'
    $http=$base.Clone();$http.OtlpHttpEndpoint='http://logs.example.invalid/otlp'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @http } 'Plain HTTP OTLP endpoint was accepted'
    $loopback=$base.Clone();$loopback.OtlpHttpEndpoint='https://localhost/otlp'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @loopback } 'Loopback OTLP endpoint was accepted'
    $inside=$base.Clone();$inside.ConfigurationFile=Join-Path $inputRoot 'collector.json'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @inside } 'Configuration inside log input was accepted'
    $missing=$base.Clone();$missing.BearerTokenFile=Join-Path $fixture 'missing.token'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @missing } 'Missing credential was accepted'
    $insideToken=Join-Path $inputRoot '101\central.token';[IO.File]::WriteAllText($insideToken,[IO.File]::ReadAllText($token))
    $unsafeToken=$base.Clone();$unsafeToken.BearerTokenFile=$insideToken
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @unsafeToken } 'Credential inside log input was accepted'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @parameters } 'Existing configuration was overwritten'
    $repository=$base.Clone();$repository.ConfigurationFile=Join-Path $PSScriptRoot 'forbidden-log-forwarder.json'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionLogForwarderConfiguration.ps1" @repository } 'Repository output path was accepted'

    [ordered]@{
        Passed=$true;Collector=(& $collector --version 2>&1|Select-Object -First 1);SchemaValidated=$true
        HttpsRequired=$true;LoopbackRemoteRejected=$true;PersistentOffsets=$true;PersistentSendingQueue=$true
        RetryUnlimited=$true;CredentialFileOnly=$true;CredentialEmbedded=$false;ListenerEnabled=$false
        InputIsolationRequired=$true;InputCredentialRejected=$true;OverwriteRejected=$true;RepositoryOutputRejected=$true
    } | ConvertTo-Json
}
finally {
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-log-forwarder-[a-f0-9]{32}$'-and(Test-Path -LiteralPath $resolved)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

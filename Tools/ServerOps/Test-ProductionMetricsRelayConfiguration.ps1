param(
    [string]$PromtoolPath = 'D:\Program Files\ET\_Env\prometheus-3.13.3\prometheus-3.13.3.windows-amd64\promtool.exe'
)

$ErrorActionPreference = 'Stop'
$tool = [IO.Path]::GetFullPath($PromtoolPath)
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Pinned promtool is not installed' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('et-metrics-relay-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $scrapeToken = Join-Path $fixture 'scrape.token'
    $writeToken = Join-Path $fixture 'write.token'
    [IO.File]::WriteAllText($scrapeToken,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    [IO.File]::WriteAllText($writeToken,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    $config = Join-Path $fixture 'relay.json'
    $parameters = @{
        HostId='game-01';Deployment='prod-cn-1';RemoteWriteUrl='https://metrics.example.invalid/api/v1/write'
        RemoteWriteTokenFile=$writeToken;OutputFile=$config;EtMetricsPort=30491;EtMetricsTokenFile=$scrapeToken
        WindowsExporter=$true;MongoExporter=$true
    }
    $report = ((& "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @parameters) -join [Environment]::NewLine) | ConvertFrom-Json
    $check = & $tool check config $config 2>&1
    if ($LASTEXITCODE -ne 0) { throw "promtool config validation failed: $($check -join ' ')" }
    $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -Depth 20
    $raw = Get-Content -LiteralPath $config -Raw
    if (-not $report.Passed -or $report.CrossHostVerified -or $parsed.scrape_configs.Count -ne 3 -or
        $parsed.global.external_labels.host_id -cne 'game-01' -or $parsed.global.external_labels.deployment -cne 'prod-cn-1' -or
        @($parsed.scrape_configs.static_configs.targets | ForEach-Object { $_ } | Where-Object { $_ -notmatch '^127\.0\.0\.1:' }).Count -ne 0 -or
        $parsed.remote_write[0].url -cne 'https://metrics.example.invalid/api/v1/write' -or
        $raw.Contains([IO.File]::ReadAllText($scrapeToken)) -or $raw.Contains([IO.File]::ReadAllText($writeToken))) {
        throw 'Generated metrics relay configuration violated the production contract'
    }

    function Expect-Rejection([scriptblock]$Action,[string]$Message) {
        $rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $Message}
    }
    $base = $parameters.Clone()
    $base.OutputFile = Join-Path $fixture 'unsafe.json'
    $http = $base.Clone();$http.RemoteWriteUrl='http://metrics.example.invalid/write'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @http } 'Plain HTTP remote write was accepted'
    $loopback = $base.Clone();$loopback.RemoteWriteUrl='https://127.0.0.1/write'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @loopback } 'Loopback remote write was accepted'
    $same = $base.Clone();$same.RemoteWriteTokenFile=$scrapeToken
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @same } 'Shared scrape/write credential was accepted'
    $duplicate = $base.Clone();$duplicate.WindowsExporterPort=30491
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @duplicate } 'Duplicate local target ports were accepted'
    $none = $base.Clone();$none.EtMetricsPort=0;$none.EtMetricsTokenFile=$null;$none.WindowsExporter=$false;$none.MongoExporter=$false
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @none } 'Configuration without a scrape target was accepted'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @parameters } 'Existing configuration was overwritten'
    $repository = $base.Clone();$repository.OutputFile=Join-Path $PSScriptRoot 'forbidden-metrics-relay.json'
    Expect-Rejection { & "$PSScriptRoot/New-ProductionMetricsRelayConfiguration.ps1" @repository } 'Repository output path was accepted'

    [ordered]@{
        Passed=$true;Promtool=(& $tool --version 2>&1|Select-Object -First 1);SchemaValidated=$true
        LocalTargetsOnly=$true;HttpsRemoteWriteRequired=$true;LoopbackRemoteRejected=$true
        SeparateCredentialsRequired=$true;CredentialsEmbedded=$false;DuplicatePortsRejected=$true
        EmptyTargetSetRejected=$true;OverwriteRejected=$true;RepositoryOutputRejected=$true
    } | ConvertTo-Json
}
finally {
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^et-metrics-relay-[a-f0-9]{32}$' -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

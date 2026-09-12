param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')

$ErrorActionPreference = 'Stop'
$version = '0.153.0'
$archiveHash = 'f5c7b1ef719660fdb930d344200c78ed576e21a56a13916f354d5e255268f5c6'
$binaryHash = '5dc314016d5dba6b1c845344dfd75c060a8ae9f52818b1f69d955eade9cab4ea'
$url = "https://github.com/open-telemetry/opentelemetry-collector-releases/releases/download/v$version/otelcol-contrib_${version}_windows_amd64.tar.gz"
$downloads = Join-Path ([IO.Path]::GetFullPath($EnvironmentRoot)) 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
$archive = Join-Path $downloads "otelcol-contrib_${version}_windows_amd64.tar.gz"
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { Invoke-WebRequest -UseBasicParsing $url -OutFile $archive }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $archiveHash) {
    throw 'OpenTelemetry Collector archive SHA256 verification failed'
}

$installRoot = Join-Path $EnvironmentRoot "otelcol-contrib-$version"
$binary = Join-Path $installRoot 'otelcol-contrib.exe'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    if (Test-Path -LiteralPath $installRoot) { throw 'OpenTelemetry Collector destination exists but is incomplete' }
    New-Item -ItemType Directory -Path $installRoot | Out-Null
    & tar.exe -xf $archive -C $installRoot
    if ($LASTEXITCODE -ne 0) { throw 'OpenTelemetry Collector extraction failed' }
}
if ((Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binaryHash) {
    throw 'Installed OpenTelemetry Collector SHA256 verification failed'
}
$reported = & $binary --version 2>&1
if ($LASTEXITCODE -ne 0 -or ($reported -join ' ') -notmatch [regex]::Escape($version)) {
    throw 'OpenTelemetry Collector executable verification failed'
}

[ordered]@{
    Version = $version
    Url = $url
    ArchiveSHA256 = $archiveHash
    BinarySHA256 = $binaryHash
    Binary = $binary
    InstalledAtUtc = [DateTime]::UtcNow.ToString('O')
} | ConvertTo-Json | Set-Content (Join-Path $EnvironmentRoot 'opentelemetry-collector.json')
Write-Host "OpenTelemetry Collector $version installed from a verified pinned artifact; no service or listener started"

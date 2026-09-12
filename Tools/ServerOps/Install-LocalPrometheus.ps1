param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
# Pinned official Windows amd64 LTS artifact; do not silently follow latest.
$version='3.13.3'
$hash='acae8f2d71baab1acac7064a5af29c40ccb9843c1e7e32baa3d5c476ae7978c9'
$url="https://github.com/prometheus/prometheus/releases/download/v$version/prometheus-$version.windows-amd64.zip"
New-Item -ItemType Directory -Force -Path "$EnvironmentRoot/downloads" | Out-Null
$archive="$EnvironmentRoot/downloads/prometheus-$version.windows-amd64.zip"
if(-not (Test-Path -LiteralPath $archive)){Invoke-WebRequest $url -OutFile $archive}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash){throw 'Prometheus SHA256 verification failed'}
$destination="$EnvironmentRoot/prometheus-$version"
if(-not (Test-Path -LiteralPath $destination)){Expand-Archive -LiteralPath $archive -DestinationPath $destination}
$binary="$destination/prometheus-$version.windows-amd64/prometheus.exe"
$tool="$destination/prometheus-$version.windows-amd64/promtool.exe"
if(-not (Test-Path -LiteralPath $binary) -or -not (Test-Path -LiteralPath $tool)){throw 'Prometheus installation incomplete'}
& $binary --version
if($LASTEXITCODE -ne 0){throw 'Prometheus executable verification failed'}
[ordered]@{Version=$version;Url=$url;SHA256=$hash;Source='https://prometheus.io/download/';InstalledAtUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content "$destination/package.json"
Write-Host 'Prometheus and promtool installed with verified archive; no service or listener started'

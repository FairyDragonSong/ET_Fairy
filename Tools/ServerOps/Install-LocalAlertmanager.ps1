param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
$version='0.34.0'
$hash='0e4b832325d6d9b737b3255a242da7b11cb7a641d7f81f7e2aabe34b3207cd79'
$url="https://github.com/prometheus/alertmanager/releases/download/v$version/alertmanager-$version.windows-amd64.zip"
New-Item -ItemType Directory -Force -Path "$EnvironmentRoot/downloads"|Out-Null
$archive="$EnvironmentRoot/downloads/alertmanager-$version.windows-amd64.zip"
if(-not(Test-Path -LiteralPath $archive)){Invoke-WebRequest $url -OutFile $archive}
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash){throw 'Alertmanager SHA256 mismatch'}
$destination="$EnvironmentRoot/alertmanager-$version"
if(-not(Test-Path -LiteralPath $destination)){Expand-Archive -LiteralPath $archive -DestinationPath $destination}
$binary="$destination/alertmanager-$version.windows-amd64/alertmanager.exe"
& $binary --version
if($LASTEXITCODE -ne 0){throw 'Alertmanager executable verification failed'}
[ordered]@{Version=$version;Url=$url;SHA256=$hash;Source='https://prometheus.io/download/';InstalledAtUtc=[DateTime]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content "$destination/package.json"
Write-Host 'Verified Alertmanager installed; no service started'

param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'

$downloads=Join-Path $EnvironmentRoot 'downloads'
New-Item -ItemType Directory -Force -Path $downloads|Out-Null

$windowsVersion='0.31.7'
$windowsHash='288252baf470da41494420250e68b5358992701298f77a36d821992b589eccdd'
$windowsUrl="https://github.com/prometheus-community/windows_exporter/releases/download/v$windowsVersion/windows_exporter-$windowsVersion-amd64.exe"
$windowsArchive=Join-Path $downloads "windows_exporter-$windowsVersion-amd64.exe"
if(-not(Test-Path -LiteralPath $windowsArchive)){Invoke-WebRequest $windowsUrl -OutFile $windowsArchive}
if((Get-FileHash -LiteralPath $windowsArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $windowsHash){throw 'windows_exporter SHA256 verification failed'}
$windowsRoot=Join-Path $EnvironmentRoot "windows-exporter-$windowsVersion"
if(-not(Test-Path -LiteralPath $windowsRoot)){New-Item -ItemType Directory -Path $windowsRoot|Out-Null}
$windowsBinary=Join-Path $windowsRoot 'windows_exporter.exe'
if(-not(Test-Path -LiteralPath $windowsBinary)){Copy-Item -LiteralPath $windowsArchive -Destination $windowsBinary}
if((Get-FileHash -LiteralPath $windowsBinary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $windowsHash){throw 'installed windows_exporter SHA256 verification failed'}

$mongoVersion='0.47.0'
$mongoHash='dc17a7e396ec0f2aca3aad8bc9100dd5b1921cd9cf77304438823ab34d9845dd'
$mongoBinaryHash='bedda7d04e3fb7ccc239d28b6f6e4216771016c1c2b2a1851d7a4b25fa05c2da'
$mongoUrl="https://github.com/percona/mongodb_exporter/releases/download/v$mongoVersion/mongodb_exporter-$mongoVersion.windows-amd64.tar.gz"
$mongoArchive=Join-Path $downloads "mongodb_exporter-$mongoVersion.windows-amd64.tar.gz"
if(-not(Test-Path -LiteralPath $mongoArchive)){Invoke-WebRequest $mongoUrl -OutFile $mongoArchive}
if((Get-FileHash -LiteralPath $mongoArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $mongoHash){throw 'mongodb_exporter SHA256 verification failed'}
$mongoRoot=Join-Path $EnvironmentRoot "mongodb-exporter-$mongoVersion"
$mongoBinary=Join-Path $mongoRoot "mongodb_exporter-$mongoVersion.windows-amd64\mongodb_exporter.exe"
if(-not(Test-Path -LiteralPath $mongoBinary)){
    if(Test-Path -LiteralPath $mongoRoot){throw 'mongodb_exporter destination exists but is incomplete'}
    New-Item -ItemType Directory -Path $mongoRoot|Out-Null
    & tar.exe -xf $mongoArchive -C $mongoRoot
    if($LASTEXITCODE -ne 0){throw 'mongodb_exporter extraction failed'}
}
if(-not(Test-Path -LiteralPath $mongoBinary)){throw 'mongodb_exporter installation incomplete'}
if((Get-FileHash -LiteralPath $mongoBinary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $mongoBinaryHash){throw 'installed mongodb_exporter SHA256 verification failed'}

& $windowsBinary --version
if($LASTEXITCODE -ne 0){throw 'windows_exporter executable verification failed'}
& $mongoBinary --version
if($LASTEXITCODE -ne 0){throw 'mongodb_exporter executable verification failed'}

[ordered]@{
    WindowsExporter=[ordered]@{Version=$windowsVersion;Url=$windowsUrl;SHA256=$windowsHash;Binary=$windowsBinary}
    MongoDBExporter=[ordered]@{Version=$mongoVersion;Url=$mongoUrl;ArchiveSHA256=$mongoHash;BinarySHA256=$mongoBinaryHash;Binary=$mongoBinary;LastUpstreamWindowsBuild=$true}
    InstalledAtUtc=[DateTime]::UtcNow.ToString('O')
}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $EnvironmentRoot 'infrastructure-exporters.json')
Write-Host 'Infrastructure exporters installed from verified pinned artifacts; no service or listener started'

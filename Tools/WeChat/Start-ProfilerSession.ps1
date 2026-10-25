param(
    [string]$ExportRoot = 'Unity/BuildArtifacts/WeChat-Profiler-20261020',
    [string]$HostAddress = '192.168.1.8',
    [ValidateRange(1,65535)][int]$ResourcePort = 8081,
    [switch]$AllowRelease
)

# Give each profile export its own resource server; avoid serving a previous release's AOT/bundles.
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exportPath = [IO.Path]::GetFullPath([IO.Path]::Combine($workspace, $ExportRoot))
if (!$exportPath.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ExportRoot must stay inside this workspace.'
}
$ip = $null
if (![Net.IPAddress]::TryParse($HostAddress, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($ip)) {
    throw 'HostAddress must be the computer LAN IPv4 address.'
}
if (!(Get-NetIPAddress -AddressFamily IPv4 | Where-Object IPAddress -eq $HostAddress)) { throw 'HostAddress is not assigned to this computer.' }
$manifest = Get-Content -LiteralPath (Join-Path $exportPath 'profiler-build.json') -Raw | ConvertFrom-Json
if ((!$manifest.development -or !$manifest.autoConnect) -and !$AllowRelease) { throw 'Expected Development + Autoconnect build, or explicit -AllowRelease.' }
$webRoot = Join-Path $exportPath 'webgl'
$versionPath = Join-Path $webRoot 'StreamingAssets/Bundles/DefaultPackage/PackageManifest_DefaultPackage.version'
if (!(Test-Path -LiteralPath $versionPath)) { throw 'Missing published StreamingAssets; finish build first.' }
if ((Get-Content -LiteralPath $versionPath -Raw).Trim() -ne $manifest.resourceVersion) { throw 'Resource version differs from build manifest.' }
if (Get-NetTCPConnection -LocalPort $ResourcePort -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $ResourcePort is already in use. Check existing service root or choose a different port."
}
$gamePath = Join-Path $exportPath 'minigame/game.js'
$source = [IO.File]::ReadAllText($gamePath)
$pattern = "DATA_CDN:\s*'[^']*'"
if ([regex]::Matches($source, $pattern).Count -ne 1) { throw 'Unexpected SDK DATA_CDN structure.' }
$backupRoot = Join-Path $exportPath 'profiler-backups'
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$backup = Join-Path $backupRoot 'game.js.original'
if (!(Test-Path -LiteralPath $backup)) { [IO.File]::Copy($gamePath, $backup, $false) }
$address = "http://${HostAddress}:$ResourcePort"
$pythonPath = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$logs = Join-Path $workspace "Temp/WeChatProfiler/servers/$ResourcePort"
[IO.Directory]::CreateDirectory($logs) | Out-Null
$server = Start-Process -FilePath $pythonPath -ArgumentList @(
    ('"' + (Join-Path $PSScriptRoot 'ProfilerHttpServer.py') + '"'), "$ResourcePort", '--bind', $HostAddress,
    '--directory', ('"' + $webRoot + '"'), '--results', ('"' + (Join-Path $workspace 'Temp/WeChatCpuExperiments/uploads') + '"'),
    '--resource-prefix', ('/__redblue__/' + $manifest.buildId)
) -WorkingDirectory $workspace -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $logs 'stdout.log') -RedirectStandardError (Join-Path $logs 'stderr.log')
try {
    # Poll for startup, then verify the exact resource version, not merely an HTTP 200.
    $ready = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ($server.HasExited) { throw "Resource server exited: $(Get-Content -LiteralPath (Join-Path $logs 'stderr.log') -Raw)" }
        try {
            $response = Invoke-WebRequest -Uri "$address/StreamingAssets/Bundles/DefaultPackage/PackageManifest_DefaultPackage.version" -TimeoutSec 2
            $version = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
            if ($version.Trim() -eq $manifest.resourceVersion) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'Resource HTTP check did not return this build version.' }
    [IO.File]::WriteAllText($gamePath, [regex]::Replace($source, $pattern, "DATA_CDN: '$address'"))
    $record = [pscustomobject]@{
        utc = [DateTime]::UtcNow.ToString('o'); processId = $server.Id; resourceUrl = $address
        resourceRoot = $webRoot; minigame = (Join-Path $exportPath 'minigame'); resourceVersion = $manifest.resourceVersion
        buildId = $manifest.buildId; gameJsSha256 = (Get-FileHash -LiteralPath $gamePath -Algorithm SHA256).Hash
        note = 'Resource HTTP verified locally; phone Profiler connection still requires QR scan and remote frames.'
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $exportPath 'profiler-session.json') -Encoding utf8
    $record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs 'service.json') -Encoding utf8
    $record | ConvertTo-Json
} catch {
    if (!$server.HasExited) { Stop-Process -Id $server.Id }
    throw
}

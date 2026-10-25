param(
    [Parameter(Mandatory)][string]$ExportRoot,
    [Parameter(Mandatory)][string]$HostAddress,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$Port,
    [Parameter(Mandatory)][ValidateRange(1,65535)][int]$OriginalPort
)

# Patch only the Profiler socket in a Development export. No engine Wasm rewriting.
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
$manifestPath = Join-Path $exportPath 'profiler-build.json'
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Missing profiler-build.json: use the Development pipeline export.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (!$manifest.development -or !$manifest.autoConnect) { throw 'Development + Autoconnect build required.' }
$files = @(Get-ChildItem -LiteralPath (Join-Path $exportPath 'minigame') -Filter '*framework*.js')
if ($files.Count -ne 1) { throw 'Expected one root framework JS.' }
if ($manifest.threads) {
    $workerFramework = Join-Path $exportPath ('minigame/workers/response/' + $files[0].Name)
    if (!(Test-Path -LiteralPath $workerFramework)) { throw 'Threaded export is missing its worker framework.' }
    $files += Get-Item -LiteralPath $workerFramework
}
$pattern = 'new WebSocketConstructor\(url,\s*opts\)'
$backupRoot = Join-Path $exportPath 'profiler-backups'
$plans = @()
for ($i = 0; $i -lt $files.Count; $i++) {
    $path = $files[$i].FullName
    $source = [IO.File]::ReadAllText($path)
    if ($source.Contains('WECHAT_PROFILER_ROUTE_V1')) { throw 'Already routed. Restore backups or export again before changing route.' }
    if ([regex]::Matches($source, $pattern).Count -ne 1) { throw "SDK socket constructor changed: $path" }
    $prefix = if ($i -eq 0) { '' } else { 'worker-' }
    $backup = Join-Path $backupRoot ($prefix + $files[$i].Name + '.profiler-original')
    if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
    $plans += [pscustomobject]@{ path = $path; source = $source; backup = $backup }
}
$hook = @"
(function(){ /* WECHAT_PROFILER_ROUTE_V1 */
 var parsed = /^(wss?):\/\/([^\/:]+):(\d+)(\/.*)?$/.exec(url);
 if(parsed && Number(parsed[3]) === $OriginalPort) {
   var original = url;
   url = 'ws://${HostAddress}:$Port' + (parsed[4] || '');
   console.info('[UnityProfiler] route', original, '=>', url);
 }
 var socket = new WebSocketConstructor(url, opts);
 if(parsed && Number(parsed[3]) === $OriginalPort) {
   if(typeof socket.addEventListener === 'function') {
     socket.addEventListener('open', function(){console.info('[UnityProfiler] connected', url);});
     socket.addEventListener('error', function(e){console.error('[UnityProfiler] error', url, e);});
     socket.addEventListener('close', function(e){console.info('[UnityProfiler] closed', url, e.code);});
   }
 }
 return socket;
})()
"@
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$patched = @()
foreach ($plan in $plans) {
    [IO.File]::Copy($plan.path, $plan.backup, $false)
    [IO.File]::WriteAllText($plan.path, [regex]::Replace($plan.source, $pattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $hook }))
    $patched += [pscustomobject]@{
        file = $plan.path; backup = $plan.backup
        originalSha256 = (Get-FileHash -LiteralPath $plan.backup -Algorithm SHA256).Hash
        routedSha256 = (Get-FileHash -LiteralPath $plan.path -Algorithm SHA256).Hash
    }
}
$record = [pscustomobject]@{
    utc = [DateTime]::UtcNow.ToString('o'); hostAddress = $HostAddress; port = $Port; originalPort = $OriginalPort
    file = $patched[0].file; backup = $patched[0].backup
    originalSha256 = $patched[0].originalSha256; routedSha256 = $patched[0].routedSha256
    files = $patched
}
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $exportPath 'profiler-route.json') -Encoding utf8
$record | ConvertTo-Json

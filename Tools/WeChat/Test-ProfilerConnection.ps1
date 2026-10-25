param(
    [string]$ExportRoot = 'Unity/BuildArtifacts/WeChat-Profiler-20261020',
    [string]$HostAddress = '192.168.1.8'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exportPath = [IO.Path]::GetFullPath([IO.Path]::Combine($workspace, $ExportRoot))
$addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
    $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*'
} | Select-Object InterfaceAlias, IPAddress)
$engines = @(Get-Process Tuanjie,Unity -ErrorAction SilentlyContinue)
$engineIds = @($engines | ForEach-Object Id)
$listeners = @(Get-NetTCPConnection -State Listen | Where-Object {
    $engineIds -contains $_.OwningProcess
} | Select-Object LocalAddress, LocalPort, OwningProcess)
$bridges = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'node.exe' -and $_.CommandLine -match 'BuildTools[\\/]websockify[\\/]websockify\.js'
} | Select-Object ProcessId, ParentProcessId, CommandLine)
$bridgeIds = @($bridges | ForEach-Object ProcessId)
$bridgeListeners = @(Get-NetTCPConnection -State Listen | Where-Object {
    $bridgeIds -contains $_.OwningProcess
} | Select-Object LocalAddress, LocalPort, OwningProcess)
$frameworks = @(Get-ChildItem -LiteralPath (Join-Path $exportPath 'minigame') -Filter '*framework*.js' -ErrorAction SilentlyContinue)
$sockets = @()
foreach ($framework in $frameworks) {
    $source = [IO.File]::ReadAllText($framework.FullName)
    $urls = @([regex]::Matches($source, 'wss?://[^\s"''<>]+') | ForEach-Object Value | Select-Object -Unique)
    $sockets += [pscustomobject]@{
        file = $framework.FullName
        literalWebSocketUrls = $urls
        hasSocketConstructor = $source -match 'new WebSocketConstructor\('
        hasProfilerRoute = $source.Contains('WECHAT_PROFILER_ROUTE_V1')
    }
}
$manifestPath = Join-Path $exportPath 'profiler-build.json'
$manifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } else { $null }
[pscustomobject]@{
    hostAddress = $HostAddress
    hostAddressExists = @($addresses | Where-Object IPAddress -eq $HostAddress).Count -gt 0
    localAddresses = $addresses
    editorProcesses = @($engines | Select-Object Id, ProcessName, Path)
    editorListeners = $listeners
    profilerWebSocketBridges = $bridges
    profilerBridgeListeners = $bridgeListeners
    buildManifest = $manifest
    frameworks = $sockets
    note = 'Local inspection only: a listening port does not prove phone connectivity. Open Unity Profiler before checking listeners. Verify the phone Player appears and remote frames arrive. Firewall/AP isolation/VPN require actual phone verification.'
} | ConvertTo-Json -Depth 8

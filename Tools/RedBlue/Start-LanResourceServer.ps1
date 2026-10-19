param(
    [string]$ExportRoot = 'Unity/BuildArtifacts/RedBlue-WeChat-20261019',
    [string]$BindAddress = '192.168.1.8',
    [int]$Port = 8080,
    [switch]$Background
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exportPath = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $ExportRoot))
if (!$exportPath.StartsWith($workspaceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ExportRoot must stay inside this workspace.'
}
$webRoot = Join-Path $exportPath 'webgl'
if (!(Test-Path -LiteralPath $webRoot -PathType Container)) { throw "Missing exported resource directory: $webRoot" }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use; leave the existing service running or choose another port."
}
$pythonPath = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (!$Background) {
    & $pythonPath -m http.server $Port --bind $BindAddress --directory $webRoot
    exit $LASTEXITCODE
}
$logRoot = Join-Path $workspaceRoot 'Temp/RedBlue-LanServer'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$serviceProcess = Start-Process -FilePath $pythonPath -ArgumentList @(
    '-m', 'http.server', "$Port", '--bind', $BindAddress, '--directory', ('"' + $webRoot + '"')
) -WorkingDirectory $workspaceRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $logRoot 'stdout.log') `
    -RedirectStandardError (Join-Path $logRoot 'stderr.log')
[pscustomobject]@{
    processId = $serviceProcess.Id
    address = "http://${BindAddress}:$Port"
    root = $webRoot
    startedUTC = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logRoot 'service.json')
Write-Output "LAN resources: http://${BindAddress}:$Port ; PID=$($serviceProcess.Id) ; root=$webRoot"

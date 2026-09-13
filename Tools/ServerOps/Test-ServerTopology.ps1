param(
    [Parameter(Mandatory)][string]$ConfigDirectory,
    [switch]$SharedLoginAdmissionEnabled
)

$ErrorActionPreference = 'Stop'

function Read-ConfigArray([string]$Name) {
    $path = Join-Path $ConfigDirectory "$Name.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing topology configuration: $Name.json"
    }
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 20
    return @($value)
}

function Assert-Unique([object[]]$Items, [scriptblock]$Key, [string]$Description) {
    $seen = @{}
    foreach ($item in $Items) {
        $value = [string](& $Key $item)
        if ([string]::IsNullOrWhiteSpace($value)) { throw "$Description contains an empty key" }
        if ($seen.ContainsKey($value)) { throw "Duplicate $Description key: $value" }
        $seen[$value] = $true
    }
}

$directory = [IO.Path]::GetFullPath($ConfigDirectory)
if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    throw "Topology configuration directory does not exist: $directory"
}

$machines = Read-ConfigArray 'StartMachineConfigCategory'
$processes = Read-ConfigArray 'StartProcessConfigCategory'
$scenes = Read-ConfigArray 'StartSceneConfigCategory'
$zones = Read-ConfigArray 'StartZoneConfigCategory'
if ($machines.Count -eq 0 -or $processes.Count -eq 0 -or $scenes.Count -eq 0 -or $zones.Count -eq 0) {
    throw 'Topology configuration categories must not be empty'
}

Assert-Unique $machines { param($x) $x.Id } 'machine id'
Assert-Unique $processes { param($x) $x.Id } 'process id'
Assert-Unique $scenes { param($x) $x.Id } 'scene id'
Assert-Unique $zones { param($x) $x.Id } 'zone id'
Assert-Unique $processes { param($x) "$($x.MachineId):$($x.Port)" } 'machine process port'
Assert-Unique $scenes { param($x) "$($x.Zone):$($x.Name)" } 'zone scene name'

$machineIds = @{}; foreach ($item in $machines) { $machineIds[[int]$item.Id] = $true }
$processById = @{}; foreach ($item in $processes) {
    if (-not $machineIds.ContainsKey([int]$item.MachineId)) { throw "Process $($item.Id) references missing machine $($item.MachineId)" }
    if ([int]$item.Port -lt 0 -or [int]$item.Port -gt 65535) { throw "Process $($item.Id) has an invalid port" }
    $processById[[int]$item.Id] = $item
}
$zoneIds = @{}; foreach ($item in $zones) { $zoneIds[[int]$item.Id] = $true }

$outerBindings = @{}
foreach ($scene in $scenes) {
    if (-not $processById.ContainsKey([int]$scene.Process)) { throw "Scene $($scene.Id) references missing process $($scene.Process)" }
    if (-not $zoneIds.ContainsKey([int]$scene.Zone)) { throw "Scene $($scene.Id) references missing zone $($scene.Zone)" }
    $port = [int]$scene.Port
    if ($port -lt 0 -or $port -gt 65535) { throw "Scene $($scene.Id) has an invalid port" }
    if ($port -gt 0) {
        $process = $processById[[int]$scene.Process]
        $binding = "$($process.MachineId):$port"
        if ($outerBindings.ContainsKey($binding)) { throw "Duplicate machine scene port: $binding" }
        $outerBindings[$binding] = $true
    }
}

$locations = @($scenes | Where-Object SceneType -eq 'Location')
if ($locations.Count -ne 1) {
    throw "Current Location state is process-local; topology must contain exactly one Location scene (found $($locations.Count))"
}

$gameplayZones = @($scenes | Where-Object { $_.SceneType -in @('Realm','Gate','Map') } | ForEach-Object { [int]$_.Zone } | Sort-Object -Unique)
foreach ($zone in $gameplayZones) {
    $zoneScenes = @($scenes | Where-Object { [int]$_.Zone -eq $zone })
    $realms = @($zoneScenes | Where-Object SceneType -eq 'Realm')
    $gates = @($zoneScenes | Where-Object SceneType -eq 'Gate')
    $maps = @($zoneScenes | Where-Object SceneType -eq 'Map')
    if ($realms.Count -lt 1 -or ($realms.Count -gt 1 -and -not $SharedLoginAdmissionEnabled)) {
        throw "Zone $zone must contain one Realm, or explicitly enable shared login admission before adding Realm instances (found $($realms.Count))"
    }
    if ($gates.Count -lt 1) { throw "Zone $zone has no Gate scene" }
    if ($maps.Count -lt 1) { throw "Zone $zone has no Map scene" }
    foreach ($entry in @($realms + $gates)) {
        if ([int]$entry.Port -eq 0) { throw "$($entry.SceneType) scene $($entry.Id) must expose a configured client port" }
    }
}

[ordered]@{
    Passed = $true
    ConfigDirectory = $directory
    Machines = $machines.Count
    Processes = $processes.Count
    Scenes = $scenes.Count
    GameplayZones = $gameplayZones.Count
    Realms = @($scenes | Where-Object SceneType -eq 'Realm').Count
    Gates = @($scenes | Where-Object SceneType -eq 'Gate').Count
    Maps = @($scenes | Where-Object SceneType -eq 'Map').Count
    Locations = $locations.Count
    SharedLoginAdmissionEnabled = [bool]$SharedLoginAdmissionEnabled
    MultiLocationRejectedUntilSharedCoordination = $true
} | ConvertTo-Json

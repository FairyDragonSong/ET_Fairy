$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ("et-topology-" + [Guid]::NewGuid().ToString('N'))

function Write-Category([string]$Name, [object[]]$Value) {
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $fixture "$Name.json") -Encoding utf8NoBOM
}

function Expect-Rejection([scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    Write-Category 'StartMachineConfigCategory' @([ordered]@{ Id = 1; InnerIP = '127.0.0.1'; OuterIP = '127.0.0.1' })
    Write-Category 'StartProcessConfigCategory' @(
        [ordered]@{ Id = 1; MachineId = 1; Port = 20001 },
        [ordered]@{ Id = 2; MachineId = 1; Port = 20002 },
        [ordered]@{ Id = 3; MachineId = 1; Port = 20003 },
        [ordered]@{ Id = 4; MachineId = 1; Port = 20004 }
    )
    Write-Category 'StartZoneConfigCategory' @([ordered]@{ Id = 1; DBConnection = 'ignored'; DBName = 'ignored' })
    $safeScenes = @(
        [ordered]@{ Id = 1; Process = 1; Zone = 1; SceneType = 'Realm'; Name = 'Realm'; Port = 30002 },
        [ordered]@{ Id = 2; Process = 2; Zone = 1; SceneType = 'Gate'; Name = 'Gate'; Port = 30003 },
        [ordered]@{ Id = 3; Process = 3; Zone = 1; SceneType = 'Location'; Name = 'Location'; Port = 0 },
        [ordered]@{ Id = 4; Process = 4; Zone = 1; SceneType = 'Map'; Name = 'Map'; Port = 0 }
    )
    Write-Category 'StartSceneConfigCategory' $safeScenes
    $safe = & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory $fixture | ConvertFrom-Json
    if (-not $safe.Passed -or $safe.Realms -ne 1 -or $safe.Locations -ne 1) { throw 'Safe topology result is invalid' }

    $bad = @($safeScenes | ForEach-Object { [ordered]@{ Id=$_.Id; Process=$_.Process; Zone=$_.Zone; SceneType=$_.SceneType; Name=$_.Name; Port=$_.Port } })
    $bad += [ordered]@{ Id = 5; Process = 4; Zone = 1; SceneType = 'Realm'; Name = 'Realm2'; Port = 30004 }
    Write-Category 'StartSceneConfigCategory' $bad
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory $fixture } 'Unsafe multi-Realm topology was accepted'
    $coordinated = & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory $fixture -SharedLoginAdmissionEnabled | ConvertFrom-Json
    if (-not $coordinated.Passed -or -not $coordinated.SharedLoginAdmissionEnabled) { throw 'Coordinated multi-Realm topology was rejected' }

    Write-Category 'StartSceneConfigCategory' $safeScenes
    $badProcesses = @(
        [ordered]@{ Id = 1; MachineId = 1; Port = 20001 },
        [ordered]@{ Id = 2; MachineId = 1; Port = 20001 },
        [ordered]@{ Id = 3; MachineId = 1; Port = 20003 },
        [ordered]@{ Id = 4; MachineId = 1; Port = 20004 }
    )
    Write-Category 'StartProcessConfigCategory' $badProcesses
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory $fixture } 'Duplicate process binding was accepted'

    Write-Category 'StartProcessConfigCategory' @(
        [ordered]@{ Id = 1; MachineId = 1; Port = 20001 },
        [ordered]@{ Id = 2; MachineId = 1; Port = 20002 },
        [ordered]@{ Id = 3; MachineId = 1; Port = 20003 },
        [ordered]@{ Id = 4; MachineId = 1; Port = 20004 }
    )
    $missingLocation = @($safeScenes | Where-Object SceneType -ne 'Location')
    Write-Category 'StartSceneConfigCategory' $missingLocation
    Expect-Rejection { & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory $fixture } 'Missing Location topology was accepted'

    $release = & (Join-Path $PSScriptRoot 'Test-ServerTopology.ps1') -ConfigDirectory (Join-Path $root 'Config/Json/s/StartConfig/Release') | ConvertFrom-Json
    if (-not $release.Passed) { throw 'Repository Release topology failed validation' }
    [ordered]@{ Passed=$true; SafeTopologyVerified=$true; MultiRealmRejected=$true; CoordinatedMultiRealmAccepted=$true; DuplicateBindingRejected=$true; MissingLocationRejected=$true; ReleaseTopologyVerified=$true } | ConvertTo-Json
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}

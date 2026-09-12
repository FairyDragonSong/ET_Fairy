$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/GameplayEvidence.ps1"

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message; expected=$Expected actual=$Actual" }
}

$last = [long]-1
Assert-Equal 'GAMEPLAY_PROGRESS connected=20 target=2000' `
    (ConvertTo-GameplayEvidenceLine '> GAMEPLAY_PROGRESS connected=20 target=2000') 'console prompt normalization'
Assert-Equal 'ordinary output' (ConvertTo-GameplayEvidenceLine 'ordinary output') 'ordinary output remains unchanged'
$cases = @(
    @{ Line = 'GAMEPLAY_PROGRESS connected=20 target=2000'; Keep = $true },
    @{ Line = 'GAMEPLAY_PROGRESS rpc_count=100 rpc_max_ms=20'; Keep = $false },
    @{ Line = 'GAMEPLAY_PROGRESS online=2000 sustained_ms=1000 rpc_count=1000 rpc_max_ms=20 working_set_bytes=1'; Keep = $true },
    @{ Line = 'GAMEPLAY_PROGRESS online=2000 sustained_ms=30000 rpc_count=2000 rpc_max_ms=20 working_set_bytes=1'; Keep = $false },
    @{ Line = 'GAMEPLAY_PROGRESS online=2000 sustained_ms=61000 rpc_count=3000 rpc_max_ms=21 working_set_bytes=1'; Keep = $true },
    @{ Line = 'GAMEPLAY_PROGRESS online=1999 sustained_ms=121000 rpc_count=4000 rpc_max_ms=21 working_set_bytes=1'; Keep = $false },
    @{ Line = 'GAMEPLAY_PROGRESS online=2000 sustained_ms=120000 rpc_count=5000 rpc_max_ms=22 working_set_bytes=1'; Keep = $true },
    @{ Line = 'GAMEPLAY_PASS clients=2000'; Keep = $true }
)

foreach ($case in $cases) {
    $actual = Test-GameplayEvidenceLine -Line $case.Line -CapacityProfile $true -TargetClients 2000 `
        -DurationMilliseconds 120000 -SampleIntervalMilliseconds 60000 -LastOnlineSampleMilliseconds ([ref]$last)
    Assert-Equal $case.Keep $actual $case.Line
}
Assert-Equal 120000 $last 'final retained sustained timestamp'

$unboundedLast = [long]-1
Assert-Equal $true (Test-GameplayEvidenceLine -Line 'GAMEPLAY_PROGRESS rpc_count=100 rpc_max_ms=20' `
    -CapacityProfile $false -TargetClients 20 -DurationMilliseconds 0 -SampleIntervalMilliseconds 60000 `
    -LastOnlineSampleMilliseconds ([ref]$unboundedLast)) 'non-capacity behavior remains unchanged'

$zeroDurationLast = [long]-1
Assert-Equal $true (Test-GameplayEvidenceLine -Line 'GAMEPLAY_PROGRESS online=100 sustained_ms=1 rpc_count=1 rpc_max_ms=1 working_set_bytes=1' `
    -CapacityProfile $true -TargetClients 100 -DurationMilliseconds 0 -SampleIntervalMilliseconds 60000 `
    -LastOnlineSampleMilliseconds ([ref]$zeroDurationLast)) 'zero-duration first sample'
Assert-Equal $false (Test-GameplayEvidenceLine -Line 'GAMEPLAY_PROGRESS online=100 sustained_ms=2 rpc_count=2 rpc_max_ms=1 working_set_bytes=1' `
    -CapacityProfile $true -TargetClients 100 -DurationMilliseconds 0 -SampleIntervalMilliseconds 60000 `
    -LastOnlineSampleMilliseconds ([ref]$zeroDurationLast)) 'zero duration must not retain every sample as final'

Write-Host 'PASS gameplay capacity evidence retention is bounded and preserves milestones'

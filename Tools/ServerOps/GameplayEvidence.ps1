function ConvertTo-GameplayEvidenceLine {
    param([AllowEmptyString()][string]$Line)

    if ($Line -match '^>\s*(GAMEPLAY_.*)$') { return $Matches[1] }
    return $Line
}

function Test-GameplayEvidenceLine {
    param(
        [Parameter(Mandatory)][string]$Line,
        [Parameter(Mandatory)][bool]$CapacityProfile,
        [Parameter(Mandatory)][int]$TargetClients,
        [Parameter(Mandatory)][long]$DurationMilliseconds,
        [Parameter(Mandatory)][long]$SampleIntervalMilliseconds,
        [Parameter(Mandatory)][ref]$LastOnlineSampleMilliseconds
    )

    if ($Line -notmatch '^GAMEPLAY_PROGRESS') { return $true }
    if (-not $CapacityProfile) { return $true }

    # Admission milestones are bounded by Clients / 20 and make partial-login
    # failures diagnosable. Bare rpc_count lines are intentionally discarded.
    if ($Line -match '^GAMEPLAY_PROGRESS connected=') { return $true }

    if ($Line -notmatch "^GAMEPLAY_PROGRESS online=$TargetClients sustained_ms=(\d+)") {
        return $false
    }

    $sustainedMilliseconds = [long]$Matches[1]
    $isFirst = $LastOnlineSampleMilliseconds.Value -lt 0
    $intervalElapsed = -not $isFirst -and
        $sustainedMilliseconds - $LastOnlineSampleMilliseconds.Value -ge $SampleIntervalMilliseconds
    $isFinal = $DurationMilliseconds -gt 0 -and $sustainedMilliseconds -ge $DurationMilliseconds
    if (-not ($isFirst -or $intervalElapsed -or $isFinal)) { return $false }

    $LastOnlineSampleMilliseconds.Value = $sustainedMilliseconds
    return $true
}

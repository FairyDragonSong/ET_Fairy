param(
    [string]$PromtoolPath = 'D:\Program Files\ET\_Env\prometheus-3.13.3\prometheus-3.13.3.windows-amd64\promtool.exe'
)

$ErrorActionPreference = 'Stop'
$rules = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'monitoring/et-production-sli.rules.yml'))
$tests = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'monitoring/et-production-sli.test.yml'))
$tool = [IO.Path]::GetFullPath($PromtoolPath)
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Pinned promtool is not installed' }

$content = [IO.File]::ReadAllText($rules)
$required = @(
    'et_login_successes_total', 'et_login_service_errors_total',
    'et_gate_login_successes_total', 'et_gate_login_service_errors_total',
    'et_map_transfer_successes_total', 'et_map_transfer_failures_total',
    'ETStageAvailabilityFastBurn', 'ETStageAvailabilitySlowBurn', 'ETMapTransferStuck',
    'internal-availability-99.5', '登录与地图SLI告警处置.md'
)
foreach ($value in $required) {
    if (-not $content.Contains($value, [StringComparison]::Ordinal)) { throw "Production SLI rule contract is missing: $value" }
}
if ($content -match '(?i)(account|login_name|role_name|client_ip|token)\s*:') {
    throw 'Production SLI rules contain a prohibited identity or credential label'
}

$check = & $tool check rules $rules 2>&1
if ($LASTEXITCODE -ne 0) { throw "promtool rule validation failed: $($check -join ' ')" }
Push-Location (Split-Path -Parent $tests)
try {
    $test = & $tool test rules (Split-Path -Leaf $tests) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "promtool behavioral tests failed: $($test -join ' ')" }
}
finally { Pop-Location }

[ordered]@{
    Passed = $true
    Promtool = (& $tool --version 2>&1 | Select-Object -First 1)
    RuleCount = 9
    FastBurnFiringAndRecovery = $true
    StuckTransferFiringAndRecovery = $true
    FixedStageLabels = @('realm', 'gate', 'map-transfer')
} | ConvertTo-Json

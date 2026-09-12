param(
    [string]$PromtoolPath = 'D:\Program Files\ET\_Env\prometheus-3.13.3\prometheus-3.13.3.windows-amd64\promtool.exe'
)

$ErrorActionPreference = 'Stop'
$monitoring = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'monitoring'))
$rules = Join-Path $monitoring 'et-infrastructure.rules.yml'
$tests = Join-Path $monitoring 'et-infrastructure.test.yml'
$config = Join-Path $monitoring 'et-infrastructure-scrape.yml'
$tool = [IO.Path]::GetFullPath($PromtoolPath)
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Pinned promtool is not installed' }

$ruleContent = [IO.File]::ReadAllText($rules)
$configContent = [IO.File]::ReadAllText($config)
$requiredAlerts = @(
    'ETWindowsHostExporterDown', 'ETMongoDBExporterDown', 'ETWindowsHighCpu',
    'ETWindowsHighMemory', 'ETWindowsDiskLow', 'ETMongoDBMemberUnhealthy',
    'ETMongoDBPrimaryCardinalityInvalid', 'ETMongoDBReplicationLag',
    'ETMongoDBConnectionSaturation'
)
foreach ($alert in $requiredAlerts) {
    if (-not $ruleContent.Contains("alert: $alert", [StringComparison]::Ordinal)) { throw "Infrastructure alert contract is missing: $alert" }
}
foreach ($metric in @(
    'windows_cpu_time_total', 'windows_memory_available_bytes', 'windows_logical_disk_free_bytes',
    'mongodb_rs_members_health', 'mongodb_rs_members_state', 'mongodb_rs_members_optimeDate',
    'mongodb_ss_connections'
)) {
    if (-not $ruleContent.Contains($metric, [StringComparison]::Ordinal)) { throw "Infrastructure metric contract is missing: $metric" }
}
if (-not $configContent.Contains('127.0.0.1:9182', [StringComparison]::Ordinal) -or
    -not $configContent.Contains('127.0.0.1:9216', [StringComparison]::Ordinal)) {
    throw 'Infrastructure exporters must remain loopback-only behind the local Prometheus collector'
}
if (-not $configContent.Contains('metric_relabel_configs', [StringComparison]::Ordinal) -or
    -not $configContent.Contains('maximum_bytes_configured', [StringComparison]::Ordinal)) {
    throw 'MongoDB operational metric allowlist is missing'
}
if (($ruleContent + $configContent) -match '(?i)(password|mongodb_uri|authsource|account|login_name|role_name|client_ip|token)\s*:') {
    throw 'Infrastructure monitoring configuration contains a prohibited credential or identity field'
}

Push-Location $monitoring
try {
    $ruleCheck = & $tool check rules (Split-Path -Leaf $rules) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "promtool rule validation failed: $($ruleCheck -join ' ')" }
    $configCheck = & $tool check config (Split-Path -Leaf $config) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "promtool config validation failed: $($configCheck -join ' ')" }
    $behavior = & $tool test rules (Split-Path -Leaf $tests) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "promtool behavioral tests failed: $($behavior -join ' ')" }
}
finally { Pop-Location }

[ordered]@{
    Passed = $true
    Promtool = (& $tool --version 2>&1 | Select-Object -First 1)
    AlertCount = $requiredAlerts.Count
    ExporterOutageFiringAndRecovery = $true
    HostResourceFiringAndRecovery = $true
    MongoDBHealthFiringAndRecovery = $true
    LoopbackScrapeContract = $true
    MongoDBMetricAllowlist = $true
} | ConvertTo-Json

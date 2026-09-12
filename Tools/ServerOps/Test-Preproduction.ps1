param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_-]+$")]
    [string]$Database,

    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_-]+_DR_TEST$")]
    [string]$RestoreDatabase,

    [Parameter(Mandatory = $true)]
    [string]$ConfirmRestoreDatabase,

    [string]$BackupOutputDirectory = (Join-Path $PSScriptRoot "backups"),

    [string]$EvidenceOutputDirectory = (Join-Path $PSScriptRoot "acceptance-results"),

    [switch]$SkipPhase1Regression,
    [Parameter(Mandatory = $true)][switch]$SourceWritesStopped
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$startedAtUtc = [DateTime]::UtcNow
if (-not $SourceWritesStopped) { throw 'Stop source writes before a consistent database-scoped backup.' }

if ($Database -ceq $RestoreDatabase) {
    throw "RestoreDatabase must differ from Database."
}
if ($ConfirmRestoreDatabase -cne $RestoreDatabase) {
    throw "ConfirmRestoreDatabase must exactly match RestoreDatabase."
}

$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI to the preproduction cluster. Credentials must not be passed as command arguments or committed."
}
if ($mongoUri -notmatch '(?i)^mongodb(\+srv)?://[^/?]+@') {
    throw "ET_MONGO_URI must contain an authenticated MongoDB authority."
}
if ($mongoUri -notmatch '(?i)[?&](tls|ssl)=true(?:&|$)') {
    throw "ET_MONGO_URI must explicitly enable verified TLS."
}
if ($mongoUri -match '(?i)[?&](tlsInsecure|tlsAllowInvalidCertificates|tlsAllowInvalidHostnames|sslAllowInvalidCertificates)=true(?:&|$)') {
    throw "ET_MONGO_URI cannot weaken TLS certificate or hostname verification."
}
if ($mongoUri -notmatch '(?i)^mongodb\+srv://' -and $mongoUri -notmatch '(?i)[?&](replicaSet=[^&]+|loadBalanced=true)(?:&|$)') {
    throw "ET_MONGO_URI must target a Replica Set, SRV cluster, or load-balanced deployment."
}
if ($mongoUri -match '(?i)[?&]retryWrites=false(?:&|$)') {
    throw "ET_MONGO_URI cannot disable retryWrites."
}

foreach ($command in @("mongosh", "mongodump", "mongorestore", "dotnet")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command is unavailable: $command"
    }
}

$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to resolve Git commit." }
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repositoryRoot status --porcelain | Out-String))
if ($dirty) { throw "Acceptance must run from a clean Git worktree so evidence identifies an exact version." }

if (-not $SkipPhase1Regression) {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "Test-Phase1.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Phase-one regression gate failed." }
}

$env:ET_ACCEPTANCE_DATABASE = $Database
try {
    $mongoCheck = & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file (Join-Path $PSScriptRoot "Test-MongoPreproduction.js")
    if ($LASTEXITCODE -ne 0) { throw "MongoDB preproduction checks failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item Env:ET_ACCEPTANCE_DATABASE -ErrorAction SilentlyContinue
}

& (Join-Path $PSScriptRoot "Audit-Mongo.ps1") -Database $Database
if ($LASTEXITCODE -ne 0) { throw "Source database consistency audit failed." }

$backupRoot = [System.IO.Path]::GetFullPath($BackupOutputDirectory)
$backupPath = & (Join-Path $PSScriptRoot "Backup-Mongo.ps1") -Database $Database -OutputDirectory $backupRoot
if ($LASTEXITCODE -ne 0) { throw "MongoDB backup failed." }
$backup = Get-Item -LiteralPath $backupPath

& (Join-Path $PSScriptRoot "Restore-Mongo.ps1") `
    -Database $RestoreDatabase `
    -BackupPath $backup.FullName `
    -ConfirmDatabaseName $ConfirmRestoreDatabase `
    -DropExisting
if ($LASTEXITCODE -ne 0) { throw "MongoDB isolated restore failed." }

$env:ET_RESTORE_SOURCE_DATABASE = $Database
$env:ET_RESTORE_TARGET_DATABASE = $RestoreDatabase
try {
    $restoreCheck = & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file (Join-Path $PSScriptRoot "Verify-MongoRestore.js")
    if ($LASTEXITCODE -ne 0) { throw "MongoDB restore verification failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item Env:ET_RESTORE_SOURCE_DATABASE -ErrorAction SilentlyContinue
    Remove-Item Env:ET_RESTORE_TARGET_DATABASE -ErrorAction SilentlyContinue
}

$evidenceRoot = [System.IO.Path]::GetFullPath($EvidenceOutputDirectory)
[System.IO.Directory]::CreateDirectory($evidenceRoot) | Out-Null
$completedAtUtc = [DateTime]::UtcNow
$evidencePath = Join-Path $evidenceRoot ("014-{0}.json" -f $completedAtUtc.ToString("yyyyMMddTHHmmssZ"))
$evidence = [ordered]@{
    Upgrade = "014"
    Commit = $commit
    StartedAtUtc = $startedAtUtc.ToString("O")
    CompletedAtUtc = $completedAtUtc.ToString("O")
    SourceDatabase = $Database
    RestoreDatabase = $RestoreDatabase
    BackupManifest = (Join-Path $backup.FullName "backup-manifest.json")
    Phase1RegressionExecuted = (-not $SkipPhase1Regression)
    MongoCheck = ($mongoCheck | Select-Object -Last 1 | ConvertFrom-Json)
    RestoreCheck = ($restoreCheck | Select-Object -Last 1 | ConvertFrom-Json)
    Result = "PASS_DATABASE_CHECKS"
    FullUpgrade014Accepted = $false
}
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8
Write-Host "PASS preproduction MongoDB, transaction, backup and isolated restore checks"
Write-Host "Evidence written to: $evidencePath"
Write-Warning "Database failover, network fault, multi-client reconnect and load testing still require environment-specific execution and evidence."

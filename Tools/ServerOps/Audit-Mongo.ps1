param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9_-]+$")]
    [string]$Database
)

$ErrorActionPreference = "Stop"
$mongoUri = $env:ET_MONGO_URI
if ([string]::IsNullOrWhiteSpace($mongoUri)) {
    throw "Set ET_MONGO_URI before running this script."
}

$env:ET_AUDIT_DATABASE = $Database
try {
    & mongosh $mongoUri --quiet --file (Join-Path $PSScriptRoot "Audit-Mongo.js")
    if ($LASTEXITCODE -ne 0) {
        throw "MongoDB account-role audit failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item Env:ET_AUDIT_DATABASE -ErrorAction SilentlyContinue
}

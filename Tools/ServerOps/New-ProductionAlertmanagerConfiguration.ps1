param(
    [Parameter(Mandatory)][string]$CriticalWebhookUrlFile,
    [Parameter(Mandatory)][string]$CriticalTokenFile,
    [Parameter(Mandatory)][string]$WarningWebhookUrlFile,
    [Parameter(Mandatory)][string]$WarningTokenFile,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"

function Assert-NoReparsePath([string]$Path,[string]$Name) {
    $cursor = if (Test-Path -LiteralPath $Path) { Get-Item -LiteralPath $Path -Force } else { Get-Item -LiteralPath (Split-Path -Parent $Path) -Force }
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Name contains a reparse point" }
        $cursor = $cursor.Parent
    }
}

function Resolve-HttpsWebhook([string]$Value,[string]$Name) {
    $path = Assert-BackupPathOutsideRepository $Value $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Name does not exist" }
    Assert-NoReparsePath $path $Name
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Length -lt 12 -or $item.Length -gt 4096) { throw "$Name has an invalid size" }
    $secretValue = [IO.File]::ReadAllText($path)
    if ($secretValue -match '\s') { throw "$Name must contain one URL without whitespace" }
    $uri = $null
    if (-not [Uri]::TryCreate($secretValue,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'https' -or
        [string]::IsNullOrWhiteSpace($uri.Host) -or -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
        throw "$Name must be an absolute HTTPS URL without user info, query or fragment"
    }
    return $uri.AbsoluteUri
}

function Resolve-BearerToken([string]$Value,[string]$Name) {
    $path = Assert-BackupPathOutsideRepository $Value $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$Name does not exist" }
    Assert-NoReparsePath $path $Name
    $item = Get-Item -LiteralPath $path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -lt 32 -or $item.Length -gt 4096) {
        throw "$Name must be a regular file between 32 and 4096 bytes"
    }
    $token = [IO.File]::ReadAllText($path)
    if ($token.Length -lt 32 -or $token -match '\s') { throw "$Name must contain one high-entropy token without whitespace" }
    return $path
}

$criticalUrl = Resolve-HttpsWebhook $CriticalWebhookUrlFile 'CriticalWebhookUrlFile'
$warningUrl = Resolve-HttpsWebhook $WarningWebhookUrlFile 'WarningWebhookUrlFile'
if ($criticalUrl -ceq $warningUrl) { throw 'Critical and warning webhook URLs must be separated' }
$criticalToken = Resolve-BearerToken $CriticalTokenFile 'CriticalTokenFile'
$warningToken = Resolve-BearerToken $WarningTokenFile 'WarningTokenFile'
if ($criticalToken -ceq $warningToken -or
    (Get-FileHash -LiteralPath $criticalToken -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $warningToken -Algorithm SHA256).Hash) {
    throw 'Critical and warning notification credentials must be separated'
}

$output = Assert-BackupPathOutsideRepository $OutputFile 'OutputFile'
if (Test-Path -LiteralPath $output) { throw 'Alertmanager configuration already exists' }
$parent = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($parent)) { throw 'OutputFile must have a parent directory' }
New-Item -ItemType Directory -Path $parent -Force | Out-Null
Assert-NoReparsePath $parent 'OutputFile parent'

function New-Webhook([string]$Url,[string]$Token) {
    return [ordered]@{
        url = $Url
        send_resolved = $true
        max_alerts = 20
        http_config = [ordered]@{
            follow_redirects = $false
            enable_http2 = $true
            authorization = [ordered]@{ type = 'Bearer'; credentials_file = $Token.Replace('\','/') }
        }
    }
}

$configuration = [ordered]@{
    global = [ordered]@{ resolve_timeout = '5m' }
    route = [ordered]@{
        receiver = 'critical-oncall'
        group_by = @('alertname','stage','component','deployment')
        group_wait = '30s'
        group_interval = '5m'
        repeat_interval = '1h'
        routes = @(
            [ordered]@{ receiver='critical-oncall';matchers=@('severity="critical"');repeat_interval='30m' },
            [ordered]@{ receiver='warning-oncall';matchers=@('severity="warning"');repeat_interval='4h' }
        )
    }
    inhibit_rules = @(
        [ordered]@{
            source_matchers = @('severity="critical"')
            target_matchers = @('severity="warning"')
            equal = @('alertname','stage','component','deployment')
        }
    )
    receivers = @(
        [ordered]@{ name='critical-oncall';webhook_configs=@(New-Webhook $criticalUrl $criticalToken) },
        [ordered]@{ name='warning-oncall';webhook_configs=@(New-Webhook $warningUrl $warningToken) }
    )
}

$bytes = $script:BackupRestoreUtf8.GetBytes(($configuration | ConvertTo-Json -Depth 16))
[IO.File]::WriteAllBytes($output,$bytes)
[ordered]@{
    Passed = $true
    ConfigurationFile = $output
    ConfigurationSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    CriticalReceiver = 'critical-oncall'
    WarningReceiver = 'warning-oncall'
    CredentialsEmbedded = $false
} | ConvertTo-Json

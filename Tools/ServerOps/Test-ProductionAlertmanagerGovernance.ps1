param(
    [string]$AmtoolPath = 'D:\Program Files\ET\_Env\alertmanager-0.34.0\alertmanager-0.34.0.windows-amd64\amtool.exe'
)

$ErrorActionPreference = 'Stop'
$tool = [IO.Path]::GetFullPath($AmtoolPath)
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw 'Pinned amtool is not installed' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('et-alertmanager-governance-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $criticalToken = Join-Path $fixture 'critical.token'
    $warningToken = Join-Path $fixture 'warning.token'
    $criticalUrl = Join-Path $fixture 'critical.url'
    $warningUrl = Join-Path $fixture 'warning.url'
    [IO.File]::WriteAllText($criticalToken,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    [IO.File]::WriteAllText($warningToken,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
    [IO.File]::WriteAllText($criticalUrl,'https://critical.example.invalid/et-alerts')
    [IO.File]::WriteAllText($warningUrl,'https://warning.example.invalid/et-alerts')
    $config = Join-Path $fixture 'alertmanager.json'
    & "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $criticalUrl `
        -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $warningUrl `
        -WarningTokenFile $warningToken -OutputFile $config | Out-Null

    $check = & $tool check-config $config 2>&1
    if ($LASTEXITCODE -ne 0) { throw "amtool config validation failed: $($check -join ' ')" }
    foreach ($case in @(
        @{Labels=@('alertname=ETStageAvailabilityFastBurn','severity=critical','stage=realm');Receiver='critical-oncall'},
        @{Labels=@('alertname=ETStageAvailabilitySlowBurn','severity=warning','stage=gate');Receiver='warning-oncall'},
        @{Labels=@('alertname=UnknownSeverity');Receiver='critical-oncall'}
    )) {
        & $tool config routes test --config.file=$config --verify.receivers=$($case.Receiver) @($case.Labels) | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Alert route did not resolve to $($case.Receiver)" }
    }
    $parsed = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json -Depth 20
    $rawConfig = Get-Content -LiteralPath $config -Raw
    if ($parsed.receivers.Count -ne 2 -or $parsed.receivers.webhook_configs.send_resolved -contains $false -or
        $rawConfig.Contains([IO.File]::ReadAllText($criticalToken)) -or $rawConfig.Contains([IO.File]::ReadAllText($warningToken))) {
        throw 'Generated notification configuration leaked credentials or weakened recovery routing'
    }

    function Expect-Rejection([scriptblock]$Action,[string]$Message) {
        $rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(-not $rejected){throw $Message}
    }
    $httpUrl=Join-Path $fixture 'http.url';[IO.File]::WriteAllText($httpUrl,'http://critical.example.invalid/alerts')
    Expect-Rejection {& "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $httpUrl -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $warningUrl -WarningTokenFile $warningToken -OutputFile (Join-Path $fixture 'http.json')} 'Plain HTTP webhook was accepted'
    Expect-Rejection {& "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $criticalUrl -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $criticalUrl -WarningTokenFile $warningToken -OutputFile (Join-Path $fixture 'same-url.json')} 'Shared severity webhook was accepted'
    Expect-Rejection {& "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $criticalUrl -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $warningUrl -WarningTokenFile $criticalToken -OutputFile (Join-Path $fixture 'same-token.json')} 'Shared notification credential was accepted'
    Expect-Rejection {& "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $criticalUrl -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $warningUrl -WarningTokenFile $warningToken -OutputFile $config} 'Existing configuration was overwritten'
    Expect-Rejection {& "$PSScriptRoot/New-ProductionAlertmanagerConfiguration.ps1" -CriticalWebhookUrlFile $criticalUrl -CriticalTokenFile $criticalToken -WarningWebhookUrlFile $warningUrl -WarningTokenFile $warningToken -OutputFile (Join-Path $PSScriptRoot 'forbidden-alertmanager.json')} 'Repository output path was accepted'

    [ordered]@{
        Passed=$true;Amtool=(& $tool --version 2>&1|Select-Object -First 1);SchemaValidated=$true
        CriticalRouteVerified=$true;WarningRouteVerified=$true;UnknownSeverityEscalates=$true
        RecoveryNotificationsRequired=$true;HttpRejected=$true;SharedEndpointRejected=$true
        SharedCredentialRejected=$true;OverwriteRejected=$true;CredentialsEmbedded=$false
        RepositoryOutputRejected=$true
    }|ConvertTo-Json
}
finally {
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-alertmanager-governance-[a-f0-9]{32}$'-and(Test-Path $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}

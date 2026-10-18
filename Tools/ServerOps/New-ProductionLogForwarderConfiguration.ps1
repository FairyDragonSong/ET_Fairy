param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$OtlpHttpEndpoint,
    [Parameter(Mandatory)][string]$BearerTokenFile,
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [ValidateSet('beginning','end')][string]$StartAt = 'end',
    [ValidatePattern('^[a-z0-9][a-z0-9._-]{1,62}$')][string]$Environment = 'production',
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$Deployment,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$HostId,
    [string]$CertificateAuthorityFile
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"

# 解析路径并确认其满足目录、存在性与类型约束。
function Resolve-SafePath([string]$Path,[string]$Name,[switch]$MustExist,[switch]$File) {
    $resolved = Assert-BackupPathOutsideRepository $Path $Name
    if ($MustExist -and -not (Test-Path -LiteralPath $resolved -PathType $(if($File){'Leaf'}else{'Container'}))) { throw "$Name does not exist" }
    $cursorPath = if (Test-Path -LiteralPath $resolved) { $resolved } else { Split-Path -Parent $resolved }
    if ([string]::IsNullOrWhiteSpace($cursorPath) -or -not (Test-Path -LiteralPath $cursorPath)) { throw "$Name parent does not exist" }
    $cursor = Get-Item -LiteralPath $cursorPath -Force
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Name contains a reparse point" }
        $cursor = $cursor.Parent
    }
    return $resolved
}
# 把 Windows 路径转换为采集器配置使用的格式。
function Convert-CollectorPath([string]$Path) { return $Path.Replace('\','/') }

$input = Resolve-SafePath $InputRoot 'InputRoot' -MustExist
$state = Resolve-SafePath $StateDirectory 'StateDirectory'
$configuration = Resolve-SafePath $ConfigurationFile 'ConfigurationFile'
if (Test-Path -LiteralPath $configuration) { throw 'ConfigurationFile already exists' }
if ($state.StartsWith($input.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or
    $configuration.StartsWith($input.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'State and configuration paths must be outside InputRoot'
}

$token = Resolve-SafePath $BearerTokenFile 'BearerTokenFile' -MustExist -File
if ($token.StartsWith($input.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'BearerTokenFile must be outside InputRoot' }
$tokenItem = Get-Item -LiteralPath $token -Force
$tokenValue = [IO.File]::ReadAllText($token)
if ($tokenItem.Length -lt 32 -or $tokenItem.Length -gt 4096 -or $tokenValue.Length -lt 32 -or $tokenValue -match '\s') {
    throw 'BearerTokenFile must contain one token between 32 and 4096 bytes without whitespace'
}

$endpoint = $null
if (-not [Uri]::TryCreate($OtlpHttpEndpoint,[UriKind]::Absolute,[ref]$endpoint) -or $endpoint.Scheme -cne 'https' -or
    [string]::IsNullOrWhiteSpace($endpoint.Host) -or -not [string]::IsNullOrEmpty($endpoint.UserInfo) -or
    -not [string]::IsNullOrEmpty($endpoint.Query) -or -not [string]::IsNullOrEmpty($endpoint.Fragment)) {
    throw 'OtlpHttpEndpoint must be an absolute HTTPS URL without user info, query or fragment'
}
$endpointAddress = $null
if ($endpoint.Host -ieq 'localhost' -or ([Net.IPAddress]::TryParse($endpoint.Host,[ref]$endpointAddress) -and [Net.IPAddress]::IsLoopback($endpointAddress))) {
    throw 'OtlpHttpEndpoint must identify a non-loopback central endpoint'
}

$tls = [ordered]@{ insecure=$false; min_version='1.2' }
if (-not [string]::IsNullOrWhiteSpace($CertificateAuthorityFile)) {
    $ca = Resolve-SafePath $CertificateAuthorityFile 'CertificateAuthorityFile' -MustExist -File
    if ($ca.StartsWith($input.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'CertificateAuthorityFile must be outside InputRoot' }
    $tls.ca_file = Convert-CollectorPath $ca
}

New-Item -ItemType Directory -Path $state -Force | Out-Null
$configParent = Split-Path -Parent $configuration
New-Item -ItemType Directory -Path $configParent -Force | Out-Null
$inputPattern = (Convert-CollectorPath $input.TrimEnd('\')) + '/*/*.log'
$archivePattern = (Convert-CollectorPath $input.TrimEnd('\')) + '/*/archive/*.log'
$config = [ordered]@{
    extensions = [ordered]@{
        file_storage = [ordered]@{ directory=Convert-CollectorPath $state }
        'bearertokenauth/central' = [ordered]@{ filename=Convert-CollectorPath $token }
    }
    receivers = [ordered]@{
        'file_log/et' = [ordered]@{
            include = @($inputPattern,$archivePattern)
            start_at = $StartAt
            include_file_path = $true
            max_log_size = '1MiB'
            storage = 'file_storage'
            multiline = [ordered]@{ line_start_pattern='^\d{4}-\d{2}-\d{2} ' }
            retry_on_failure = [ordered]@{ enabled=$true; max_elapsed_time='0' }
            resource = [ordered]@{
                'service.name' = 'et-server'
                'deployment.environment.name' = $Environment
                'service.instance.id' = "$HostId-$Deployment"
                'host.id' = $HostId
            }
            operators = @(
                [ordered]@{ type='regex_parser';regex='^(?P<event_time>[^|]+)\|(?P<severity_text>[^|]+)\|(?P<logger>[^|]+)\|(?P<message>(?s:.*))$';parse_to='attributes';on_error='drop' },
                [ordered]@{ type='move';from='attributes.message';to='body' },
                [ordered]@{ id='parse_correlation';type='regex_parser';parse_from='body';parse_to='attributes';regex='(?s)^(?:.*\s)?correlation_id=(?P<correlation_id>[A-Za-z0-9._-]{1,80})(?:\s.*)?$';if='body matches "correlation_id=[A-Za-z0-9._-]{1,80}"';on_error='drop' },
                [ordered]@{ type='severity_parser';parse_from='attributes.severity_text';on_error='send' }
            )
        }
    }
    processors = [ordered]@{
        memory_limiter = [ordered]@{ check_interval='1s';limit_mib=256;spike_limit_mib=64 }
        'transform/redact' = [ordered]@{
            error_mode='propagate'
            log_statements=@([ordered]@{ context='log';statements=@('replace_pattern(body, "(?i)(password|token|secret|authorization|cookie)=([^\\s]+)", "$1=[REDACTED]") where IsString(body)') })
        }
        batch = [ordered]@{ timeout='1s';send_batch_size=128 }
    }
    exporters = [ordered]@{
        'otlp_http/central' = [ordered]@{
            endpoint = $endpoint.AbsoluteUri.TrimEnd('/')
            auth = [ordered]@{ authenticator='bearertokenauth/central' }
            tls = $tls
            timeout = '30s'
            retry_on_failure = [ordered]@{ enabled=$true;initial_interval='1s';max_interval='30s';max_elapsed_time='0' }
            sending_queue = [ordered]@{ enabled=$true;num_consumers=2;queue_size=2048;storage='file_storage' }
        }
    }
    service = [ordered]@{
        telemetry = [ordered]@{ logs=[ordered]@{level='warn'};metrics=[ordered]@{level='none'} }
        extensions = @('file_storage','bearertokenauth/central')
        pipelines = [ordered]@{ logs=[ordered]@{ receivers=@('file_log/et');processors=@('memory_limiter','transform/redact','batch');exporters=@('otlp_http/central') } }
    }
}

$bytes = $script:BackupRestoreUtf8.GetBytes(($config | ConvertTo-Json -Depth 14))
[IO.File]::WriteAllBytes($configuration,$bytes)
[ordered]@{
    Passed=$true;ConfigurationFile=$configuration
    ConfigurationSha256=(Get-FileHash -LiteralPath $configuration -Algorithm SHA256).Hash.ToLowerInvariant()
    InputRoot=$input;StateDirectory=$state;EndpointScheme=$endpoint.Scheme;EndpointHost=$endpoint.Host
    StartAt=$StartAt;PersistentOffsets=$true;PersistentSendingQueue=$true;SecretsEmbedded=$false
    ListenerEnabled=$false;CrossHostVerified=$false
} | ConvertTo-Json

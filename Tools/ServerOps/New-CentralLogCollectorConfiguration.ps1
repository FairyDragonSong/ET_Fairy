param(
    [Parameter(Mandatory)][string]$InputRoot,
    [Parameter(Mandatory)][string]$StateDirectory,
    [Parameter(Mandatory)][string]$OutputFile,
    [Parameter(Mandatory)][string]$ConfigurationFile,
    [switch]$LocalEvidenceSink,
    [ValidateSet('beginning', 'end')][string]$StartAt = 'end',
    [ValidatePattern('^[a-z0-9][a-z0-9._-]{1,62}$')][string]$Environment = 'production',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$Deployment = 'et-production',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$HostId = $env:COMPUTERNAME
)

$ErrorActionPreference = 'Stop'
if (-not $LocalEvidenceSink) { throw 'This generator only supports an explicit local evidence sink' }
# 解析路径并确认其满足目录、存在性与类型约束。
function Resolve-SafePath([string]$Path, [string]$Name) {
    if (-not [IO.Path]::IsPathRooted($Path)) { throw "$Name must be an absolute path" }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.TrimEnd('\') -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "$Name cannot be a volume root" }
    $cursor = $full
    while ($cursor -and (Test-Path -LiteralPath $cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "$Name cannot traverse a reparse point" }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}
# 把 Windows 路径转换为采集器配置使用的格式。
function Convert-CollectorPath([string]$Path) { return $Path.Replace('\', '/') }

$resolvedInput = Resolve-SafePath $InputRoot 'InputRoot'
$state = Resolve-SafePath $StateDirectory 'StateDirectory'
$output = Resolve-SafePath $OutputFile 'OutputFile'
$configuration = Resolve-SafePath $ConfigurationFile 'ConfigurationFile'
if (-not (Test-Path -LiteralPath $resolvedInput -PathType Container)) { throw 'InputRoot does not exist' }
if ($output.StartsWith($resolvedInput.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputFile cannot be inside InputRoot' }
if (Test-Path -LiteralPath $output) { throw 'OutputFile already exists' }
if (Test-Path -LiteralPath $configuration) { throw 'ConfigurationFile already exists' }
foreach ($directory in @($state, (Split-Path -Parent $output), (Split-Path -Parent $configuration))) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$inputPattern = (Convert-CollectorPath $resolvedInput.TrimEnd('\')) + '/*/*.log'
$archivePattern = (Convert-CollectorPath $resolvedInput.TrimEnd('\')) + '/*/archive/*.log'
$config = [ordered]@{
    extensions = [ordered]@{
        file_storage = [ordered]@{ directory = Convert-CollectorPath $state }
    }
    receivers = [ordered]@{
        'file_log/et' = [ordered]@{
            include = @($inputPattern, $archivePattern)
            start_at = $StartAt
            include_file_path = $true
            max_log_size = '1MiB'
            storage = 'file_storage'
            multiline = [ordered]@{ line_start_pattern = '^\d{4}-\d{2}-\d{2} ' }
            retry_on_failure = [ordered]@{ enabled = $true; max_elapsed_time = '0' }
            resource = [ordered]@{
                'service.name' = 'et-server'
                'deployment.environment.name' = $Environment
                'service.instance.id' = "$HostId-$Deployment"
            }
            operators = @(
                [ordered]@{
                    type = 'regex_parser'
                    regex = '^(?P<event_time>[^|]+)\|(?P<severity_text>[^|]+)\|(?P<logger>[^|]+)\|(?P<message>(?s:.*))$'
                    parse_to = 'attributes'
                    on_error = 'drop'
                },
                [ordered]@{ type = 'move'; from = 'attributes.message'; to = 'body' },
                [ordered]@{
                    id = 'parse_correlation'
                    type = 'regex_parser'
                    parse_from = 'body'
                    parse_to = 'attributes'
                    regex = '(?s)^(?:.*\s)?correlation_id=(?P<correlation_id>[A-Za-z0-9._-]{1,80})(?:\s.*)?$'
                    if = 'body matches "correlation_id=[A-Za-z0-9._-]{1,80}"'
                    on_error = 'drop'
                },
                [ordered]@{ type = 'severity_parser'; parse_from = 'attributes.severity_text'; on_error = 'send' }
            )
        }
    }
    processors = [ordered]@{
        memory_limiter = [ordered]@{ check_interval = '1s'; limit_mib = 256; spike_limit_mib = 64 }
        'transform/redact' = [ordered]@{
            error_mode = 'propagate'
            log_statements = @(
                [ordered]@{
                    context = 'log'
                    statements = @('replace_pattern(body, "(?i)(password|token|secret|authorization|cookie)=([^\\s]+)", "$1=[REDACTED]") where IsString(body)')
                }
            )
        }
        batch = [ordered]@{ timeout = '1s'; send_batch_size = 128 }
    }
    exporters = [ordered]@{
        file = [ordered]@{
            path = Convert-CollectorPath $output
            create_directory = $true
            rotation = [ordered]@{ max_megabytes = 64; max_days = 7; max_backups = 20; localtime = $false }
        }
    }
    service = [ordered]@{
        telemetry = [ordered]@{
            logs = [ordered]@{ level = 'warn' }
            metrics = [ordered]@{ level = 'none' }
        }
        extensions = @('file_storage')
        pipelines = [ordered]@{
            logs = [ordered]@{
                receivers = @('file_log/et')
                processors = @('memory_limiter', 'transform/redact', 'batch')
                exporters = @('file')
            }
        }
    }
}
$json = $config | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($configuration, $json, [Text.UTF8Encoding]::new($false))
[ordered]@{
    ConfigurationFile = $configuration
    ConfigurationSHA256 = (Get-FileHash -LiteralPath $configuration -Algorithm SHA256).Hash.ToLowerInvariant()
    InputRoot = $resolvedInput
    StateDirectory = $state
    OutputFile = $output
    StartAt = $StartAt
    ListenerEnabled = $false
    SecretsEmbedded = $false
    ProductionBackend = $false
} | ConvertTo-Json

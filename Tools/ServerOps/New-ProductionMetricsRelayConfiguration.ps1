param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$HostId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{1,79}$')][string]$Deployment,
    [Parameter(Mandatory)][string]$RemoteWriteUrl,
    [Parameter(Mandatory)][string]$RemoteWriteTokenFile,
    [Parameter(Mandatory)][string]$OutputFile,
    [ValidateRange(0,65535)][int]$EtMetricsPort = 0,
    [string]$EtMetricsTokenFile,
    [switch]$WindowsExporter,
    [ValidateRange(1,65535)][int]$WindowsExporterPort = 9182,
    [switch]$MongoExporter,
    [ValidateRange(1,65535)][int]$MongoExporterPort = 9216,
    [string]$CertificateAuthorityFile
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"

# 拒绝路径链上的重解析点，防止符号链接逃逸。
function Assert-NoReparsePath([string]$Path,[string]$Name) {
    $cursor = if (Test-Path -LiteralPath $Path) { Get-Item -LiteralPath $Path -Force } else { Get-Item -LiteralPath (Split-Path -Parent $Path) -Force }
    while ($null -ne $cursor) {
        if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "$Name contains a reparse point" }
        $cursor = $cursor.Parent
    }
}

# 解析并校验指标采集凭据文件。
function Resolve-CredentialFile([string]$Path,[string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Name is required" }
    $resolved = Assert-BackupPathOutsideRepository $Path $Name
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Name does not exist" }
    Assert-NoReparsePath $resolved $Name
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Length -lt 32 -or $item.Length -gt 4096) { throw "$Name must be a regular file between 32 and 4096 bytes" }
    $value = [IO.File]::ReadAllText($resolved)
    if ($value.Length -lt 32 -or $value -match '\s') { throw "$Name must contain one token without whitespace" }
    return $resolved
}

$remote = $null
if (-not [Uri]::TryCreate($RemoteWriteUrl,[UriKind]::Absolute,[ref]$remote) -or $remote.Scheme -cne 'https' -or
    [string]::IsNullOrWhiteSpace($remote.Host) -or -not [string]::IsNullOrEmpty($remote.UserInfo) -or
    -not [string]::IsNullOrEmpty($remote.Query) -or -not [string]::IsNullOrEmpty($remote.Fragment)) {
    throw 'RemoteWriteUrl must be an absolute HTTPS URL without user info, query or fragment'
}
$remoteAddress = $null
if ($remote.Host -ieq 'localhost' -or ([Net.IPAddress]::TryParse($remote.Host,[ref]$remoteAddress) -and [Net.IPAddress]::IsLoopback($remoteAddress))) {
    throw 'RemoteWriteUrl must identify a non-loopback central endpoint'
}

$remoteToken = Resolve-CredentialFile $RemoteWriteTokenFile 'RemoteWriteTokenFile'
$etToken = $null
if ($EtMetricsPort -gt 0) { $etToken = Resolve-CredentialFile $EtMetricsTokenFile 'EtMetricsTokenFile' }
elseif (-not [string]::IsNullOrWhiteSpace($EtMetricsTokenFile)) { throw 'EtMetricsTokenFile requires EtMetricsPort' }
if ($EtMetricsPort -eq 0 -and -not $WindowsExporter -and -not $MongoExporter) { throw 'At least one local scrape target is required' }
if ($null -ne $etToken -and (([IO.Path]::GetFullPath($etToken) -ieq [IO.Path]::GetFullPath($remoteToken)) -or
    ((Get-FileHash -LiteralPath $etToken -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $remoteToken -Algorithm SHA256).Hash))) {
    throw 'Local scrape and central write credentials must be separated'
}
$ports = @()
if ($EtMetricsPort -gt 0) { $ports += $EtMetricsPort }
if ($WindowsExporter) { $ports += $WindowsExporterPort }
if ($MongoExporter) { $ports += $MongoExporterPort }
if (@($ports | Select-Object -Unique).Count -ne $ports.Count) { throw 'Local scrape target ports must be distinct' }

$scrapes = [Collections.Generic.List[object]]::new()
if ($EtMetricsPort -gt 0) {
    $scrapes.Add([ordered]@{
        job_name = 'et-server'
        authorization = [ordered]@{ type='Bearer'; credentials_file=$etToken.Replace('\','/') }
        static_configs = @([ordered]@{ targets=@("127.0.0.1:$EtMetricsPort") })
    })
}
if ($WindowsExporter) {
    $scrapes.Add([ordered]@{ job_name='windows'; static_configs=@([ordered]@{ targets=@("127.0.0.1:$WindowsExporterPort") }) })
}
if ($MongoExporter) {
    $scrapes.Add([ordered]@{ job_name='mongodb'; static_configs=@([ordered]@{ targets=@("127.0.0.1:$MongoExporterPort") }) })
}

$remoteWrite = [ordered]@{
    name = 'central'
    url = $remote.AbsoluteUri
    remote_timeout = '30s'
    follow_redirects = $false
    enable_http2 = $true
    authorization = [ordered]@{ type='Bearer'; credentials_file=$remoteToken.Replace('\','/') }
}
if (-not [string]::IsNullOrWhiteSpace($CertificateAuthorityFile)) {
    $ca = Assert-BackupPathOutsideRepository $CertificateAuthorityFile 'CertificateAuthorityFile'
    if (-not (Test-Path -LiteralPath $ca -PathType Leaf)) { throw 'CertificateAuthorityFile does not exist' }
    Assert-NoReparsePath $ca 'CertificateAuthorityFile'
    $remoteWrite.tls_config = [ordered]@{ ca_file=$ca.Replace('\','/'); min_version='TLS12' }
}

$configuration = [ordered]@{
    global = [ordered]@{
        scrape_interval = '15s'
        scrape_timeout = '10s'
        external_labels = [ordered]@{ host_id=$HostId; deployment=$Deployment }
    }
    scrape_configs = @($scrapes)
    remote_write = @($remoteWrite)
}

$output = Assert-BackupPathOutsideRepository $OutputFile 'OutputFile'
if (Test-Path -LiteralPath $output) { throw 'Metrics relay configuration already exists' }
$parent = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($parent)) { throw 'OutputFile must have a parent directory' }
New-Item -ItemType Directory -Path $parent -Force | Out-Null
Assert-NoReparsePath $parent 'OutputFile parent'
$bytes = $script:BackupRestoreUtf8.GetBytes(($configuration | ConvertTo-Json -Depth 12))
[IO.File]::WriteAllBytes($output,$bytes)
[ordered]@{
    Passed = $true
    ConfigurationFile = $output
    ConfigurationSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    HostId = $HostId
    Deployment = $Deployment
    ScrapeJobs = @($scrapes.job_name)
    RemoteWriteScheme = $remote.Scheme
    RemoteWriteHost = $remote.Host
    LocalTargetsOnly = $true
    CredentialsEmbedded = $false
    CrossHostVerified = $false
} | ConvertTo-Json

param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')

$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Install-LocalOpenTelemetryCollector.ps1" -EnvironmentRoot $EnvironmentRoot
$collector = Join-Path $EnvironmentRoot 'otelcol-contrib-0.153.0\otelcol-contrib.exe'
$fixture = Join-Path ([IO.Path]::GetFullPath($EnvironmentRoot)) ('central-log-test-' + [Guid]::NewGuid().ToString('N'))
$inputRoot = Join-Path $fixture 'bounded'
$state = Join-Path $fixture 'state'
$central = Join-Path $fixture 'central'
$config = Join-Path $fixture 'collector.json'
New-Item -ItemType Directory -Path (Join-Path $inputRoot '101'), (Join-Path $inputRoot '202'), $central | Out-Null
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls.exe $fixture /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect central log fixture' }

$markerA = 'central-a-' + [Guid]::NewGuid().ToString('N')
$markerB = 'central-b-' + [Guid]::NewGuid().ToString('N')
$secret = 'forbidden-' + [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllLines((Join-Path $inputRoot '101\info.log'), @(
    "2026-09-12 12:00:00.0000|Info|Realm|event=login_result correlation_id=req-a result=success marker=$markerA",
    "2026-09-12 12:00:01.0000|Warn|Realm|event=test password=$secret"
), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $inputRoot '202\error.log'), "2026-09-12 12:00:02.0000|Error|Gate|event=gate_failure correlation_id=req-b marker=$markerB`r`nstack-continuation-preserved`r`n", [Text.UTF8Encoding]::new($false))

& "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
    -OutputFile (Join-Path $central 'logs.json') -ConfigurationFile $config -LocalEvidenceSink -StartAt beginning `
    -Environment preproduction -Deployment test-deployment -HostId test-host | Out-Null
& $collector validate --config $config
if ($LASTEXITCODE -ne 0) { throw 'Central log collector configuration validation failed' }

$process = $null
$stdoutTask = $null
$stderrTask = $null
$passed = $false
$firstStdout = ''
$firstStderr = ''
$secondStdout = ''
$secondStderr = ''
$stdout = ''
$stderr = ''
$offsetReplayPrevented = $false
$unsafePathRejections = 0
function Read-SharedText([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}
function Assert-Rejected([scriptblock]$Action, [string]$Name, [string]$ExpectedMessage) {
    try { & $Action; throw "Unsafe central log configuration was accepted: $Name" }
    catch {
        if ($_.Exception.Message.StartsWith('Unsafe central log configuration was accepted:', [StringComparison]::Ordinal)) { throw }
        if (-not $_.Exception.Message.Contains($ExpectedMessage, [StringComparison]::Ordinal)) { throw }
        $script:unsafePathRejections++
    }
}
try {
    $start = [Diagnostics.ProcessStartInfo]::new($collector)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add($config)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Central log collector did not start' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $output = Join-Path $central 'logs.json'
    $text = ''
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw "Central log collector exited early: $($stderrTask.Result)" }
        if (Test-Path -LiteralPath $output) {
            $text = Read-SharedText $output
            if ($text.Contains($markerA) -and $text.Contains($markerB) -and $text.Contains('[REDACTED]')) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $text.Contains($markerA) -or -not $text.Contains($markerB)) { throw 'Logs from both process directories were not centralized' }
    if ($text.Contains($secret)) { throw 'Sensitive value was not redacted from centralized output' }
    foreach ($required in @('[REDACTED]', 'correlation_id=req-a', 'correlation_id=req-b', '"key":"correlation_id"', 'stack-continuation-preserved', 'preproduction', 'test-host-test-deployment', 'log.file.path')) {
        if (-not $text.Contains($required)) { throw "Centralized log evidence is missing: $required" }
    }
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue)
    if ($listeners.Count -ne 0) { throw 'Central log collector unexpectedly opened a listener' }

    $process.Kill($true)
    $process.WaitForExit()
    $firstStdout = [string]$stdoutTask.Result
    $firstStderr = [string]$stderrTask.Result
    $process.Dispose()
    $process = $null
    $stdoutTask = $null
    $stderrTask = $null

    $markerC = 'central-c-' + [Guid]::NewGuid().ToString('N')
    [IO.File]::AppendAllText((Join-Path $inputRoot '101\info.log'), "2026-09-12 12:00:03.0000|Info|Realm|event=restart marker=$markerC`r`n", [Text.UTF8Encoding]::new($false))
    $restartConfig = Join-Path $fixture 'collector-restart.json'
    $restartOutput = Join-Path $central 'logs-restart.json'
    & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
        -OutputFile $restartOutput -ConfigurationFile $restartConfig -LocalEvidenceSink -StartAt beginning `
        -Environment preproduction -Deployment test-deployment -HostId test-host | Out-Null
    & $collector validate --config $restartConfig
    if ($LASTEXITCODE -ne 0) { throw 'Restart collector configuration validation failed' }

    $start = [Diagnostics.ProcessStartInfo]::new($collector)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--config')
    $start.ArgumentList.Add($restartConfig)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw 'Restarted central log collector did not start' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $restartText = ''
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($process.HasExited) { throw "Restarted central log collector exited early: $($stderrTask.Result)" }
        if (Test-Path -LiteralPath $restartOutput) {
            $restartText = Read-SharedText $restartOutput
            if ($restartText.Contains($markerC)) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $restartText.Contains($markerC)) { throw 'Restarted collector did not collect the appended log' }
    if ($restartText.Contains($markerA) -or $restartText.Contains($markerB)) { throw 'Persistent offsets replayed logs already exported before restart' }
    $offsetReplayPrevented = $true
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue)
    if ($listeners.Count -ne 0) { throw 'Restarted central log collector unexpectedly opened a listener' }
    $process.Kill($true)
    $process.WaitForExit()
    $secondStdout = [string]$stdoutTask.Result
    $secondStderr = [string]$stderrTask.Result
    $process.Dispose()
    $process = $null
    $stdoutTask = $null
    $stderrTask = $null
    if (($firstStderr + $secondStderr) -match '(?im)(deprecated|\terror\t)') { throw 'Central log collector emitted a deprecation or processing error' }
    Assert-Rejected {
        & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
            -OutputFile (Join-Path $inputRoot 'recursive.log') -ConfigurationFile (Join-Path $fixture 'reject-recursive.json') -LocalEvidenceSink
    } 'recursive output' 'OutputFile cannot be inside InputRoot'
    Assert-Rejected {
        & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot ([IO.Path]::GetPathRoot($inputRoot)) -StateDirectory $state `
            -OutputFile (Join-Path $central 'root.log') -ConfigurationFile (Join-Path $fixture 'reject-root.json') -LocalEvidenceSink
    } 'volume root input' 'InputRoot cannot be a volume root'
    Assert-Rejected {
        & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
            -OutputFile (Join-Path $central 'logs.json') -ConfigurationFile (Join-Path $fixture 'reject-overwrite.json') -LocalEvidenceSink
    } 'output overwrite' 'OutputFile already exists'
    Assert-Rejected {
        & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
            -OutputFile (Join-Path $central 'fresh.log') -ConfigurationFile $config -LocalEvidenceSink
    } 'configuration overwrite' 'ConfigurationFile already exists'
    Assert-Rejected {
        & "$PSScriptRoot/New-CentralLogCollectorConfiguration.ps1" -InputRoot $inputRoot -StateDirectory $state `
            -OutputFile (Join-Path $central 'not-local.log') -ConfigurationFile (Join-Path $fixture 'reject-not-local.json')
    } 'implicit local sink' 'only supports an explicit local evidence sink'
    $passed = $true
    Write-Host "PASS centralized logs from two process directories, persistent offsets, structured correlation, multiline, no listener, redaction and $unsafePathRejections unsafe-path rejections"
}
finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $stdout = if ($stdoutTask) { [string]$stdoutTask.Result } else { '' }
        $stderr = if ($stderrTask) { [string]$stderrTask.Result } else { '' }
        $process.Dispose()
    }
    $report = [ordered]@{
        Commit = (& git -C ([IO.Path]::GetFullPath("$PSScriptRoot/../..")) rev-parse HEAD).Trim()
        Passed = $passed
        CompletedAtUtc = [DateTime]::UtcNow.ToString('O')
        CollectorVersion = '0.153.0'
        ProcessDirectories = 2
        RedactionVerified = $passed
        CorrelationSearchVerified = $passed
        PersistentOffsetRestartVerified = $offsetReplayPrevented
        ListenerCount = if ($listeners) { $listeners.Count } else { 0 }
        UnsafePathRejections = $unsafePathRejections
        Configuration = $config
        CentralOutput = Join-Path $central 'logs.json'
        ProductionBackendVerified = $false
        CollectorStandardOutput = $firstStdout + $secondStdout + $stdout
        CollectorStandardError = $firstStderr + $secondStderr + $stderr
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $fixture 'result.json')
    Write-Host "Evidence: $(Join-Path $fixture 'result.json')"
}

param([switch]$SkipRuntimeSmoke)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

& dotnet build (Join-Path $repositoryRoot "DotNet\DotNet.sln") -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "server build failed" }

& dotnet run --project (Join-Path $repositoryRoot "DotNet\Tests\DotNet.Tests.csproj") -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "phase-one regression tests failed" }

if ($SkipRuntimeSmoke) { exit 0 }

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Join-Path $repositoryRoot "Bin\App.exe")
$startInfo.WorkingDirectory = (Join-Path $repositoryRoot "Bin")
$startInfo.Arguments = "--Process=8 --StartConfig=StartConfig/Release --Console=1 --ShutdownTimeoutSeconds=5"
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo

try {
    if (-not $process.Start()) { throw "failed to start RouterManager smoke process" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $live = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $live = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:30300/health/live" -TimeoutSec 1
            break
        }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if ($null -eq $live -or $live.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($live.Headers["X-Request-Id"])) {
        throw "liveness smoke test failed"
    }

    $ready = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:30300/health/ready" -TimeoutSec 2
    $metrics = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:30300/metrics" -TimeoutSec 2
    if ($ready.StatusCode -ne 200 -or -not $metrics.Content.Contains("et_shutdown_requested")) {
        throw "readiness or metrics smoke test failed"
    }

    $process.StandardInput.WriteLine("shutdown")
    if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) {
        throw "graceful shutdown smoke test failed"
    }
    Write-Host "PASS RouterManager health, metrics and graceful shutdown smoke test"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    $process.Dispose()
}

$guardInfo = [System.Diagnostics.ProcessStartInfo]::new()
$guardInfo.FileName = (Join-Path $repositoryRoot "Bin\App.exe")
$guardInfo.WorkingDirectory = (Join-Path $repositoryRoot "Bin")
$guardInfo.Arguments = "--Process=1 --StartConfig=StartConfig/Release --Console=0 --Develop=0"
$guardInfo.UseShellExecute = $false
$guardInfo.RedirectStandardOutput = $true
$guardInfo.RedirectStandardError = $true
$guardInfo.Environment.Remove("ET_MONGO_URI") | Out-Null
$guardInfo.Environment.Remove("ET_MONGO_URI_ZONE_1") | Out-Null
$guardProcess = [System.Diagnostics.Process]::new()
$guardProcess.StartInfo = $guardInfo
try {
    if (-not $guardProcess.Start()) { throw "failed to start production guard process" }
    if (-not $guardProcess.WaitForExit(10000)) { throw "unsafe production configuration did not terminate" }
    if ($guardProcess.ExitCode -ne 3) {
        $guardError = $guardProcess.StandardError.ReadToEnd()
        throw "production guard expected exit 3 but received $($guardProcess.ExitCode): $guardError"
    }
    Write-Host "PASS unsafe Release Realm configuration failed closed with exit code 3"
}
finally {
    if (-not $guardProcess.HasExited) {
        $guardProcess.Kill($true)
        $guardProcess.WaitForExit()
    }
    $guardProcess.Dispose()
}

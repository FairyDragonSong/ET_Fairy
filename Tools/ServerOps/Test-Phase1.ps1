param([switch]$SkipRuntimeSmoke)

$ErrorActionPreference = "Stop"
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

& (Join-Path $PSScriptRoot 'Test-GameplayEvidence.ps1')

& dotnet build (Join-Path $repositoryRoot "DotNet\DotNet.sln") -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "server build failed" }

& dotnet run --project (Join-Path $repositoryRoot "DotNet\Tests\DotNet.Tests.csproj") -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "phase-one regression tests failed" }

if ($SkipRuntimeSmoke) { exit 0 }

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = (Join-Path $repositoryRoot "Bin\App.exe")
$startInfo.WorkingDirectory = (Join-Path $repositoryRoot "Bin")
$startInfo.Arguments = "--Process=8 --StartConfig=StartConfig/Release --Console=1 --Develop=1 --ShutdownTimeoutSeconds=5"
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
    if($live.Headers['Cache-Control'] -ne 'no-store' -or $live.Headers['X-Content-Type-Options'] -ne 'nosniff' -or
       $live.Headers['Content-Security-Policy'] -ne "default-src 'none'" -or $live.Headers['Referrer-Policy'] -ne 'no-referrer'){
        throw 'RouterManager security headers missing'
    }

    $ready = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:30300/health/ready" -TimeoutSec 2
    $metrics = Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:30300/metrics" -TimeoutSec 2
    if ($ready.StatusCode -ne 200 -or -not $metrics.Content.Contains("et_shutdown_requested")) {
        throw "readiness or metrics smoke test failed"
    }
    if(-not $metrics.Content.Contains("# TYPE et_session_journal_end_failures_total counter") -or
       -not $metrics.Content.Contains("et_password_change_accepted_total 0") -or
       -not $metrics.Content.Contains("et_shutdown_emergency 0")){
        throw 'Security counters missing from metrics HTTP response'
    }
    $methodRejected=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:30300/get_router' -Method POST -SkipHttpErrorCheck -TimeoutSec 2
    $queryRejected=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:30300/health/live?debug=1' -SkipHttpErrorCheck -TimeoutSec 2
    if($methodRejected.StatusCode -ne 405 -or $methodRejected.Headers['Allow'] -ne 'GET' -or $queryRejected.StatusCode -ne 400){
        throw 'RouterManager method or query hardening failed'
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

$secretGuardInfo = [System.Diagnostics.ProcessStartInfo]::new()
$secretGuardInfo.FileName = (Join-Path $repositoryRoot "Bin\App.exe")
$secretGuardInfo.WorkingDirectory = (Join-Path $repositoryRoot "Bin")
$secretGuardInfo.Arguments = "--Process=1 --StartConfig=StartConfig/Release --Console=0 --Develop=0 --PasswordLoginEnabled=1 --PasswordLoginTransportSecure=1"
$secretGuardInfo.UseShellExecute = $false
$secretGuardInfo.RedirectStandardOutput = $true
$secretGuardInfo.RedirectStandardError = $true
$secretGuardInfo.Environment.Remove("ET_SECRET_ROOT") | Out-Null
$secretGuardInfo.Environment.Remove("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE") | Out-Null
$secretGuardProcess = [System.Diagnostics.Process]::new()
$secretGuardProcess.StartInfo = $secretGuardInfo
try {
    if (-not $secretGuardProcess.Start()) { throw "failed to start production Secret guard process" }
    if (-not $secretGuardProcess.WaitForExit(10000)) { throw "missing production Secret did not terminate" }
    if ($secretGuardProcess.ExitCode -ne 3) {
        $secretGuardError = $secretGuardProcess.StandardError.ReadToEnd()
        throw "production Secret guard expected exit 3 but received $($secretGuardProcess.ExitCode): $secretGuardError"
    }
    $secretGuardError = $secretGuardProcess.StandardError.ReadToEnd()
    if ($secretGuardError.Contains("PRIVATE KEY") -or $secretGuardError.Contains("BEGIN ")) {
        throw "production Secret guard output disclosed credential details"
    }
    Write-Host "PASS missing production password Secret failed closed with exit code 3"
}
finally {
    if (-not $secretGuardProcess.HasExited) {
        $secretGuardProcess.Kill($true)
        $secretGuardProcess.WaitForExit()
    }
    $secretGuardProcess.Dispose()
}

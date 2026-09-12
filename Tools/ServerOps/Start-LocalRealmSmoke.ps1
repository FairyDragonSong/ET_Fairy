param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/LocalMongo.ps1" -Action Use -EnvironmentRoot $EnvironmentRoot
# ET's .NET driver uses the Windows trust store. Trust only this test CA for this user.
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content -LiteralPath $env:ET_MONGO_CA_FILE -Raw))
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root','CurrentUser')
$store.Open('ReadWrite'); $store.Add($certificate); $store.Close()
$certificate.Thumbprint | Set-Content "$env:ET_LOCAL_MONGO_ROOT/ca-thumbprint.txt"
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$info = [Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
$info.WorkingDirectory = "$repo/Bin"
$info.Arguments = '--Process=1 --StartConfig=StartConfig/Release --Console=1 --Develop=0 --ShutdownTimeoutSeconds=5'
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
$process = [Diagnostics.Process]::new(); $process.StartInfo=$info
try {
    if (-not $process.Start()) { throw 'Realm failed to start' }
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    Start-Sleep -Seconds 8
    if ($process.HasExited) { throw "Realm exited early: $($process.ExitCode)" }
    $process.StandardInput.WriteLine('shutdown')
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Realm shutdown failed' }
    Write-Host 'PASS production-mode Realm startup, TLS MongoDB connection, migrations and graceful shutdown'
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}

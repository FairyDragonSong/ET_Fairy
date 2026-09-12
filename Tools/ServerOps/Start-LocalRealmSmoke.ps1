param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
# ET uses ET_MONGO_CA_FILE as an explicit custom root; no OS trust-store mutation.
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/../..")
$info = [Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
$info.WorkingDirectory = "$repo/Bin"
$info.Arguments = '--Process=1 --StartConfig=StartConfig/Release --Console=1 --Develop=0 --ShutdownTimeoutSeconds=5'
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardInput=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
$info.Environment.Remove('ET_GAMEPLAY_LOOPBACK') | Out-Null
foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
$process = [Diagnostics.Process]::new(); $process.StartInfo=$info
try {
    if (-not $process.Start()) { throw 'Realm failed to start' }
    $stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine('startup-check')
    $ready=$false
    $deadline=[DateTime]::UtcNow.AddSeconds(40)
    while([DateTime]::UtcNow -lt $deadline) {
        $line=$process.StandardOutput.ReadLineAsync()
        if(-not $line.Wait([TimeSpan]::FromSeconds(35))){break}
        if($null -eq $line.Result){break}
        if($line.Result -match 'STARTUP_READY'){$ready=$true;break}
    }
    if(-not $ready){throw 'Realm did not confirm completed startup; process liveness is insufficient'}
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $process.StandardInput.WriteLine('shutdown')
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Realm shutdown failed' }
    Write-Host 'PASS production-mode Realm startup, TLS MongoDB connection, migrations and graceful shutdown'
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}

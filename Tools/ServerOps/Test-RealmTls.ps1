param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
& "$PSScriptRoot/Start-LocalRealmSmoke.ps1" -EnvironmentRoot $EnvironmentRoot
. "$PSScriptRoot/LocalMongo.ps1" -Action Use -EnvironmentRoot $EnvironmentRoot
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$wrongCa="$env:ET_LOCAL_MONGO_ROOT/untrusted-test-ca.pem"
if(-not(Test-Path -LiteralPath $wrongCa)){throw 'Run local database security tests to generate unrelated CA fixture'}
$info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
$info.WorkingDirectory="$repo/Bin"
$info.Arguments='--Process=1 --StartConfig=StartConfig/Release --Develop=0 --Console=0'
$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.Environment.Remove('ET_GAMEPLAY_LOOPBACK') | Out-Null
$info.Environment['ET_MONGO_CA_FILE']=$wrongCa
$info.Environment['ET_MONGO_URI']=$env:ET_MONGO_URI+'&serverSelectionTimeoutMS=2000&connectTimeoutMS=1000'
foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
$process=[Diagnostics.Process]::new();$process.StartInfo=$info
try{
    if(-not $process.Start()){throw 'Negative TLS test failed to start'}
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(15000) -or $process.ExitCode -ne 3){throw 'Untrusted CA did not fail production Realm startup'}
    Write-Host 'PASS .NET Realm explicitly trusted CA accepted; unrelated CA rejected with exit code 3'
}finally{
    if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $process.Dispose()
}

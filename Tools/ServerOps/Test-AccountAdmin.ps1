param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
& "$PSScriptRoot/Test-Gameplay.ps1" -EnvironmentRoot $EnvironmentRoot -Baseline -Clients 1
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$fixture=Join-Path $env:ET_LOCAL_MONGO_ROOT ('admin-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$private=Join-Path $fixture 'operator-private.pem'
$rsa=[Security.Cryptography.RSA]::Create(3072)
$process=$null
function Read-Target {
    $json=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Get-LocalAdminTarget.js"
    if($LASTEXITCODE -ne 0){throw 'Cannot inspect isolated admin target'}
    return ($json|ConvertFrom-Json)
}
function Send-Command([string]$path,[string]$expected) {
    $process.StandardInput.WriteLine("account-admin $path")
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    while([DateTime]::UtcNow -lt $deadline){
        $line=$process.StandardOutput.ReadLineAsync()
        if(-not $line.Wait(45000)){throw 'Admin response timed out'}
        if($null -eq $line.Result){throw 'Admin process ended unexpectedly'}
        if($line.Result -match 'ACCOUNT_ADMIN_RESULT (\w+)'){
            if($Matches[1] -ne $expected){throw "Unexpected admin result: $($Matches[1])"}
            Write-Host "PASS signed admin command: $expected"
            return
        }
    }
    throw 'Admin result missing'
}
try {
    [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem())
    $trust=Join-Path $fixture 'trust.json'
    $deployment='local-admin-'+[Guid]::NewGuid().ToString('N')
    ConvertTo-Json -InputObject @(@{Id='local-operator';PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem();Database='ET_LOCAL_GAMEPLAY';DeploymentId=$deployment;Zone=1;Actions=@('disable','enable');ExpiresAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+3600}) | Set-Content $trust
    $target=Read-Target
    $disable=Join-Path $fixture 'disable.json'
    & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action disable -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $target.Version -ReasonCode local-test -OutputFile $disable
    $tampered=Get-Content $disable -Raw|ConvertFrom-Json
    $bytes=[Convert]::FromBase64String($tampered.Signature);$bytes[0]=$bytes[0] -bxor 1
    $tampered.Signature=[Convert]::ToBase64String($bytes)
    $bad=Join-Path $fixture 'tampered.json';$tampered|ConvertTo-Json|Set-Content $bad
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe")
    $info.WorkingDirectory="$repo/Bin"
    $info.Arguments='--Process=1 --StartConfig=StartConfig/Localhost --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1 --Console=1 --ShutdownTimeoutSeconds=20'
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['ET_ACCOUNT_ADMIN_TRUST_FILE']=$trust
    $info.Environment['ET_ACCOUNT_ADMIN_DEPLOYMENT_ID']=$deployment
    foreach($name in @($info.Environment.Keys)){if($name -match '^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info
    if(-not $process.Start()){throw 'Admin server start failed'}
    $stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine('startup-check')
    do {
        $readyLine=$process.StandardOutput.ReadLineAsync()
        if(-not $readyLine.Wait(40000) -or $null -eq $readyLine.Result){throw 'Admin startup readiness missing'}
        if($readyLine.Result -match 'STARTUP_NOT_READY'){throw 'Admin test server is not ready'}
    } until($readyLine.Result -match 'STARTUP_READY')
    Send-Command $bad 'REJECTED'
    $unchanged=Read-Target
    if($unchanged.Status -ne 0 -or $unchanged.Version -ne $target.Version -or $unchanged.AuditCount -ne 0){throw 'Invalid signature changed account'}
    Send-Command $disable 'APPLIED'
    Send-Command $disable 'ALREADY_APPLIED'
    $disabled=Read-Target
    if($disabled.Status -ne 1 -or [long]$disabled.Version -ne ([long]$target.Version+1) -or $disabled.AuditCount -ne 1){throw 'Disable or idempotent audit failed'}
    $enable=Join-Path $fixture 'enable.json'
    & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action enable -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $disabled.Version -ReasonCode local-test -OutputFile $enable
    Send-Command $enable 'APPLIED'
    $restored=Read-Target
    if($restored.Status -ne 0 -or [long]$restored.Version -ne ([long]$target.Version+2) -or $restored.AuditCount -ne 2){throw 'Enable audit failed'}
    $process.StandardInput.WriteLine('shutdown')
    $stdout=$process.StandardOutput.ReadToEndAsync()
    if(-not $process.WaitForExit(25000) -or $process.ExitCode -ne 0){throw 'Admin server shutdown failed'}
    @{Passed=$true;Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Checks=@('tampered-signature-no-write','signed-disable','idempotent-repeat','signed-enable','two-audit-records')}|ConvertTo-Json|Set-Content (Join-Path $fixture 'result.json')
    Write-Host 'PASS signed administration end-to-end; isolated account restored'
} finally {
    $rsa.Dispose()
    if($process){if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
    # Exact file created by this invocation inside the ACL-protected local fixture directory.
    if(Test-Path -LiteralPath $private){Remove-Item -LiteralPath $private}
    Remove-Item Env:ET_GAMEPLAY_PASSWORD,Env:ET_GAMEPLAY_ACCOUNT,Env:ET_GAMEPLAY_ROLEID -ErrorAction SilentlyContinue
}

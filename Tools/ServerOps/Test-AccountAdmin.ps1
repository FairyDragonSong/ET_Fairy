param([string]$EnvironmentRoot='D:\Program Files\ET\_Env',[switch]$Lifecycle)
$ErrorActionPreference='Stop'
& "$PSScriptRoot/Test-Gameplay.ps1" -EnvironmentRoot $EnvironmentRoot -Baseline -Clients 1
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$fixture=Join-Path $env:ET_LOCAL_MONGO_ROOT ('admin-test-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$private=Join-Path $fixture 'operator-private.pem'
$rsa=[Security.Cryptography.RSA]::Create(3072)
$process=$null
$roleCreated=$false
$rootUri=$env:ET_MONGO_URI
function Read-Target {
    $json=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Get-LocalAdminTarget.js"
    if($LASTEXITCODE -ne 0){throw 'Cannot inspect isolated admin target'}
    return ($json|ConvertFrom-Json)
}
function Send-Command([string]$path,[string]$expected,[switch]$Quiet) {
    $process.StandardInput.WriteLine("account-admin $path")
    $deadline=[DateTime]::UtcNow.AddSeconds(45)
    while([DateTime]::UtcNow -lt $deadline){
        $line=$process.StandardOutput.ReadLineAsync()
        if(-not $line.Wait(45000)){throw 'Admin response timed out'}
        if($null -eq $line.Result){throw 'Admin process ended unexpectedly'}
        if($line.Result -match 'ACCOUNT_ADMIN_RESULT (\w+)'){
            if($Matches[1] -ne $expected){throw "Unexpected admin result: $($Matches[1])"}
            if(-not $Quiet){Write-Host "PASS signed admin command: $expected"}
            return
        }
    }
    throw 'Admin result missing'
}
try {
    [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem())
    $trust=Join-Path $fixture 'trust.json'
    $deployment='local-admin-'+[Guid]::NewGuid().ToString('N')
    $env:ET_ADMIN_TEST_DEPLOYMENT=$deployment
    ConvertTo-Json -InputObject @(@{Id='local-operator';PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem();Database='ET_LOCAL_GAMEPLAY';DeploymentId=$deployment;Zone=1;Actions=$(if($Lifecycle){@('disable','enable','revoke','close')}else{@('disable','enable')});ExpiresAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+3600}) | Set-Content $trust
    $target=Read-Target
    $env:ET_ADMIN_TEST_USER='et_admin_test_'+[Guid]::NewGuid().ToString('N')
    $env:ET_ADMIN_TEST_PASSWORD=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $roleCreated=$true
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"
    if($LASTEXITCODE -ne 0){throw 'Limited admin role provisioning failed'}
    $limitedUri="mongodb://$($env:ET_ADMIN_TEST_USER):$($env:ET_ADMIN_TEST_PASSWORD)@localhost:27101,localhost:27102,localhost:27103/ET_LOCAL_GAMEPLAY?authSource=ET_LOCAL_GAMEPLAY&replicaSet=et-local-rs&tls=true&retryWrites=true"
    try {
        $env:ET_MONGO_URI=$limitedUri
        & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Test-AdminDatabasePermissions.js"
        if($LASTEXITCODE -ne 0){throw 'Admin database privilege checks failed'}
    } finally {$env:ET_MONGO_URI=$rootUri}
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
    $info.Environment['ET_ACCOUNT_ADMIN_MONGO_URI']=$limitedUri
    $info.Environment['ET_ACCOUNT_ADMIN_DATABASE']='ET_LOCAL_GAMEPLAY'
    $info.Environment['ET_ACCOUNT_ADMIN_ZONE']='1'
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
    if($unchanged.Status -ne 0 -or $unchanged.Version -ne $target.Version -or $unchanged.AuditCount -ne 0 -or $unchanged.RejectedAttempts -ne 1){throw 'Invalid signature write protection or rejection audit failed'}
    Send-Command $disable 'APPLIED'
    Send-Command $disable 'ALREADY_APPLIED'
    $disabled=Read-Target
    if($disabled.Status -ne 1 -or [long]$disabled.Version -ne ([long]$target.Version+1) -or $disabled.AuditCount -ne 1){throw 'Disable or idempotent audit failed'}
    $enable=Join-Path $fixture 'enable.json'
    & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action enable -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $disabled.Version -ReasonCode local-test -OutputFile $enable
    Send-Command $enable 'APPLIED'
    $restored=Read-Target
    if($restored.Status -ne 0 -or [long]$restored.Version -ne ([long]$target.Version+2) -or $restored.AuditCount -ne 2){throw 'Enable audit failed'}
    if($Lifecycle){
        $revoke=Join-Path $fixture 'revoke.json'
        & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action revoke -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $restored.Version -ReasonCode local-test -OutputFile $revoke
        Send-Command $revoke 'APPLIED'
        $revoked=Read-Target
        if($revoked.Status -ne 0 -or [long]$revoked.Version -ne ([long]$target.Version+3) -or $revoked.AuditCount -ne 3){throw 'Explicit revoke failed'}
        $close=Join-Path $fixture 'close.json'
        & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action close -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $revoked.Version -ReasonCode local-test -OutputFile $close
        Send-Command $close 'APPLIED'
        Send-Command $close 'ALREADY_APPLIED'
        $closed=Read-Target
        if($closed.Status -ne 2 -or [long]$closed.Version -ne ([long]$target.Version+4) -or $closed.AuditCount -ne 4 -or [long]$closed.ClosedAt -le 0){throw 'Soft close failed'}
        $reopen=Join-Path $fixture 'reopen.json'
        & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action enable -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $closed.Version -ReasonCode local-test -OutputFile $reopen
        Send-Command $reopen 'REJECTED'
        $stillClosed=Read-Target
        if($stillClosed.Status -ne 2 -or $stillClosed.Version -ne $closed.Version -or $stillClosed.AuditCount -ne 4){throw 'Closed account was reopened'}
        Write-Host 'PASS signed revoke, soft close, close replay and reopen rejection'
    }else{
    $blocked=Join-Path $fixture 'audit-unavailable.json'
    & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-operator -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action disable -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $restored.Version -ReasonCode local-test -OutputFile $blocked
    $env:ET_ADMIN_TEST_AUDIT_POLICY='deny'
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"
    if($LASTEXITCODE -ne 0){throw 'Cannot inject audit write denial'}
    Send-Command $blocked 'REJECTED'
    $noAudit=Read-Target
    if($noAudit.Status -ne 0 -or $noAudit.Version -ne $restored.Version -or $noAudit.AuditCount -ne 2){throw 'Management changed state without required attempt audit'}
    $env:ET_ADMIN_TEST_AUDIT_POLICY='allow'
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"
    if($LASTEXITCODE -ne 0){throw 'Cannot restore audit permission'}
    Write-Host 'PASS audit unavailable blocks state changes'
    for($i=0;$i -lt 31;$i++){Send-Command $bad 'REJECTED' -Quiet}
    $limited=Read-Target
    if($limited.LimitedAttempts -lt 1 -or $limited.RejectedAttempts -ne 32 -or $limited.AuditCount -ne 2 -or $limited.Version -ne $restored.Version){throw 'Management rate limit or durable rejection audit failed'}
    Write-Host 'PASS administration burst limited and every rejected request audited'
    }
    $process.StandardInput.WriteLine('shutdown')
    $stdout=$process.StandardOutput.ReadToEndAsync()
    if(-not $process.WaitForExit(25000) -or $process.ExitCode -ne 0){throw 'Admin server shutdown failed'}
    $checks=@('tampered-signature-no-write','signed-disable','idempotent-repeat','signed-enable','audit-mutation-denied','account-delete-denied','role-access-denied')
    $checks += $(if($Lifecycle){@('signed-revoke','signed-close','close-idempotency','closed-cannot-enable')}else{@('two-audit-records','audit-unavailable-no-state-write','rate-limited','rejection-audits-persisted')})
    @{Passed=$true;Lifecycle=[bool]$Lifecycle;Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Checks=$checks}|ConvertTo-Json|Set-Content (Join-Path $fixture 'result.json')
    Write-Host $(if($Lifecycle){'PASS lifecycle administration; isolated account retained as Closed'}else{'PASS signed administration end-to-end; isolated account restored'})
} catch {
    @{Passed=$false;Commit=(& git -C $repo rev-parse HEAD).Trim();CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Reason='See test console failure; no command or credential content retained'}|ConvertTo-Json|Set-Content (Join-Path $fixture 'result.json')
    throw
} finally {
    $rsa.Dispose()
    if($process){if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
    $env:ET_MONGO_URI=$rootUri
    if($roleCreated){
        $env:ET_ADMIN_TEST_CLEANUP='1'
        & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"
        if($LASTEXITCODE -ne 0){Write-Warning 'Test administration identity cleanup failed; remove the per-test identity manually'}
    }
    Remove-Item Env:ET_ADMIN_TEST_USER,Env:ET_ADMIN_TEST_PASSWORD,Env:ET_ADMIN_TEST_CLEANUP,Env:ET_ADMIN_TEST_DEPLOYMENT,Env:ET_ADMIN_TEST_AUDIT_POLICY -ErrorAction SilentlyContinue
    # Exact file created by this invocation inside the ACL-protected local fixture directory.
    if(Test-Path -LiteralPath $private){Remove-Item -LiteralPath $private}
    Remove-Item Env:ET_GAMEPLAY_PASSWORD,Env:ET_GAMEPLAY_ACCOUNT,Env:ET_GAMEPLAY_ROLEID -ErrorAction SilentlyContinue
}

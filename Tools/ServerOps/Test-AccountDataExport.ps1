param([string]$EnvironmentRoot='D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
& "$PSScriptRoot/Test-Gameplay.ps1" -EnvironmentRoot $EnvironmentRoot -Baseline -Clients 1
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$fixture=Join-Path $env:ET_LOCAL_MONGO_ROOT ('account-export-test-'+[Guid]::NewGuid().ToString('N'))
$exportRoot=Join-Path $fixture 'exports';New-Item -ItemType Directory -Path $exportRoot -Force|Out-Null
$private=Join-Path $fixture 'operator-private.pem';$rsa=[Security.Cryptography.RSA]::Create(3072)
$process=$null;$roleCreated=$false;$rootUri=$env:ET_MONGO_URI
function Send-Command([string]$path,[string]$expected){
    $process.StandardInput.WriteLine("account-admin $path")
    while($true){
        $line=$process.StandardOutput.ReadLineAsync();if(-not $line.Wait(45000)-or $null-eq$line.Result){throw 'Export response timed out'}
        if($line.Result-match'ACCOUNT_ADMIN_RESULT (\w+)'){if($Matches[1]-ne$expected){throw "Unexpected export result: $($Matches[1])"};return}
    }
}
try{
    [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem())
    $trust=Join-Path $fixture 'trust.json';$deployment='local-export-'+[Guid]::NewGuid().ToString('N')
    ConvertTo-Json -InputObject @(@{Id='local-exporter';PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem();Database='ET_LOCAL_GAMEPLAY';DeploymentId=$deployment;Zone=1;Actions=@('export');ExpiresAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()+3600}) -Depth 5|Set-Content $trust
    $targetJson=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Get-LocalAdminTarget.js"
    if($LASTEXITCODE-ne 0){throw 'Cannot inspect export target'};$target=$targetJson|ConvertFrom-Json
    $env:ET_ADMIN_TEST_USER='et_admin_test_'+[Guid]::NewGuid().ToString('N')
    $env:ET_ADMIN_TEST_PASSWORD=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $env:ET_ADMIN_TEST_EXPORT='1';$roleCreated=$true
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"
    if($LASTEXITCODE-ne 0){throw 'Export identity provisioning failed'}
    $limitedUri="mongodb://$($env:ET_ADMIN_TEST_USER):$($env:ET_ADMIN_TEST_PASSWORD)@localhost:27101,localhost:27102,localhost:27103/ET_LOCAL_GAMEPLAY?authSource=ET_LOCAL_GAMEPLAY&replicaSet=et-local-rs&tls=true&retryWrites=true"
    try{$env:ET_MONGO_URI=$limitedUri;& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Test-AccountDataExportPermissions.js";if($LASTEXITCODE-ne 0){throw 'Export least privilege failed'}}finally{$env:ET_MONGO_URI=$rootUri}
    $operation=[Guid]::NewGuid().ToString('N');$env:ET_ADMIN_TEST_OPERATION=$operation
    $command=Join-Path $fixture 'export.json'
    & "$PSScriptRoot/New-AccountAdminCommand.ps1" -PrivateKeyFile $private -OperatorId local-exporter -DeploymentId $deployment -Database ET_LOCAL_GAMEPLAY -Zone 1 -Action export -AccountId $target.AccountId -ConfirmAccountId $target.AccountId -ExpectedVersion $target.Version -ReasonCode user-request -OperationId $operation -OutputFile $command
    $tampered=Get-Content $command -Raw|ConvertFrom-Json;$signature=[Convert]::FromBase64String($tampered.Signature);$signature[0]=$signature[0]-bxor 1;$tampered.Signature=[Convert]::ToBase64String($signature)
    $bad=Join-Path $fixture 'tampered.json';$tampered|ConvertTo-Json|Set-Content $bad
    $info=[Diagnostics.ProcessStartInfo]::new("$repo/Bin/App.exe");$info.WorkingDirectory="$repo/Bin"
    $info.Arguments='--Process=1 --StartConfig=StartConfig/Localhost --Develop=1 --PasswordLoginEnabled=1 --AccountAutoRegister=1 --Console=1 --ShutdownTimeoutSeconds=20'
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $info.Environment['ET_ACCOUNT_ADMIN_TRUST_FILE']=$trust;$info.Environment['ET_ACCOUNT_ADMIN_DEPLOYMENT_ID']=$deployment
    $info.Environment['ET_ACCOUNT_ADMIN_MONGO_URI']=$limitedUri;$info.Environment['ET_ACCOUNT_ADMIN_DATABASE']='ET_LOCAL_GAMEPLAY';$info.Environment['ET_ACCOUNT_ADMIN_ZONE']='1';$info.Environment['ET_ACCOUNT_EXPORT_ROOT']=$exportRoot
    foreach($name in @($info.Environment.Keys)){if($name-match'^ET_MONGO_(URI|DATABASE)_ZONE_'){$info.Environment.Remove($name)|Out-Null}}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$info;if(-not$process.Start()){throw 'Export server start failed'};$stderr=$process.StandardError.ReadToEndAsync()
    $process.StandardInput.WriteLine('startup-check');do{$ready=$process.StandardOutput.ReadLineAsync();if(-not$ready.Wait(40000)-or$null-eq$ready.Result){throw 'Export startup readiness missing'}}until($ready.Result-match'STARTUP_READY')
    Send-Command $bad 'REJECTED';if(Test-Path -LiteralPath (Join-Path $exportRoot "$operation.json")){throw 'Tampered command created an export'}
    Send-Command $command 'APPLIED';Send-Command $command 'ALREADY_APPLIED'
    $output=Join-Path $exportRoot "$operation.json";$raw=[IO.File]::ReadAllText($output);$data=$raw|ConvertFrom-Json
    if([string]$data.AccountId-ne[string]$target.AccountId-or$data.Account.StateVersion-ne$target.Version-or@($data.Roles).Count-ne 1-or@($data.Sessions).Count-lt 1){throw 'Export content is incomplete'}
    if($raw-match'Password(Hash|Salt|Scheme|Iterations)|EncryptedPassword|CredentialDigest|PrivateKey|Token|GateInstanceId'){throw 'Credential or internal topology material appeared in export'}
    $auditJson=& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Get-LocalDataExportAudit.js";if($LASTEXITCODE-ne 0){throw 'Cannot inspect export audit'};$audit=$auditJson|ConvertFrom-Json
    $digest=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    if($audit.AuditCount-ne 1-or$audit.Action-ne'export'-or$audit.Sha256-ne$digest){throw 'Export audit or digest mismatch'}
    $process.StandardInput.WriteLine('shutdown');$stdout=$process.StandardOutput.ReadToEndAsync();if(-not$process.WaitForExit(25000)-or$process.ExitCode-ne 0){throw 'Export server shutdown failed'}
    @{Passed=$true;Commit=(& git -C $repo rev-parse HEAD).Trim();WorktreeDirty=[bool](& git -C $repo status --porcelain);CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Checks=@('export-only-database-identity','tampered-command-rejected','credential-fields-excluded','role-and-session-included','idempotent-retry','sha256-audit')}|ConvertTo-Json|Set-Content (Join-Path $fixture 'result.json')
    Write-Host "PASS signed account data export: $output"
}finally{
    $rsa.Dispose();if($process){if(-not$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()};$env:ET_MONGO_URI=$rootUri
    if($roleCreated){$env:ET_ADMIN_TEST_CLEANUP='1';& mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/LocalAdminDatabaseRole.js"|Out-Host}
    Remove-Item Env:ET_ADMIN_TEST_USER,Env:ET_ADMIN_TEST_PASSWORD,Env:ET_ADMIN_TEST_EXPORT,Env:ET_ADMIN_TEST_CLEANUP,Env:ET_ADMIN_TEST_OPERATION -ErrorAction SilentlyContinue
    if(Test-Path -LiteralPath $private){Remove-Item -LiteralPath $private};Remove-Item Env:ET_GAMEPLAY_PASSWORD,Env:ET_GAMEPLAY_ACCOUNT,Env:ET_GAMEPLAY_ROLEID -ErrorAction SilentlyContinue
}

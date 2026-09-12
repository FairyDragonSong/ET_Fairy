param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')
$ErrorActionPreference='Stop'
. "$PSScriptRoot/LocalMongo.ps1" -Action Use -EnvironmentRoot $EnvironmentRoot
$env:ET_RESTORE_SOURCE_DATABASE='ET_LOCAL_ACCEPTANCE'
$env:ET_RESTORE_TARGET_DATABASE='ET_LOCAL_ACCEPTANCE_DR_TEST'
$evidenceRoot="$env:ET_LOCAL_MONGO_ROOT/evidence"
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$key=[System.Security.Cryptography.RSA]::Create(2048)
$req=[System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Unrelated Test CA',$key,[System.Security.Cryptography.HashAlgorithmName]::SHA256,[System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$req.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
$cert=$req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddDays(2))
$env:ET_UNTRUSTED_CA_FILE="$env:ET_LOCAL_MONGO_ROOT/untrusted-test-ca.pem"
$cert.ExportCertificatePem() | Set-Content $env:ET_UNTRUSTED_CA_FILE -Encoding ascii
$cert.Dispose();$key.Dispose()
function Invoke-Check([string]$File) {
    $output = & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/$File"
    if($LASTEXITCODE -ne 0){throw "$File failed"}
    return ($output | Select-Object -Last 1 | ConvertFrom-Json)
}
# mongosh also uses the Windows trust store. Temporarily remove only our test CA
# to test an unrelated CA, then restore its original trusted state in finally.
$trusted=[System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem((Get-Content $env:ET_MONGO_CA_FILE -Raw))
$trustStore=[System.Security.Cryptography.X509Certificates.X509Store]::new('Root','CurrentUser')
$trustStore.Open('ReadWrite')
$wasTrusted=$trustStore.Certificates.Find('FindByThumbprint',$trusted.Thumbprint,$false).Count -gt 0
try {
    if($wasTrusted){$trustStore.Remove($trusted)}
    $security=Invoke-Check 'Test-LocalMongoSecurity.js'
} finally {
    if($wasTrusted){$trustStore.Add($trusted)}
    $trustStore.Close();$trusted.Dispose()
}
$failover=Invoke-Check 'Test-LocalMongoFailover.js'
# Preserve the backup as the reference before forcing down this test environment only.
$before=Invoke-Check 'Verify-MongoRestore.js'
& "$PSScriptRoot/LocalMongo.ps1" -Action Crash -EnvironmentRoot $EnvironmentRoot
Start-Sleep -Seconds 2
& "$PSScriptRoot/LocalMongo.ps1" -Action Start -EnvironmentRoot $EnvironmentRoot
$after=Invoke-Check 'Verify-MongoRestore.js'
if(($before.counts | ConvertTo-Json -Depth 6 -Compress) -ne ($after.counts | ConvertTo-Json -Depth 6 -Compress)){throw 'Data changed across cluster crash/restart'}
# Change one synthetic restored role without changing count, then prove the verifier rejects it.
try {
    $env:ET_FIXTURE_MUTATION='damage'
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Mutate-LocalRestoreFixture.js"
    if($LASTEXITCODE -ne 0){throw 'Unable to mutate isolated fixture'}
    $negative = & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Verify-MongoRestore.js" 2>&1
    if($LASTEXITCODE -eq 0 -or ($negative | Out-String) -notmatch 'document content mismatch'){throw 'Restore verifier did not reject same-count content damage'}
} finally {
    $env:ET_FIXTURE_MUTATION='repair'
    & mongosh --nodb --quiet --file "$PSScriptRoot/Connect-Mongo.js" --file "$PSScriptRoot/Mutate-LocalRestoreFixture.js"
    if($LASTEXITCODE -ne 0){throw 'Failed to restore isolated fixture'}
}
$null=Invoke-Check 'Verify-MongoRestore.js'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/../..")
$revision=(& git -C $repo rev-parse HEAD).Trim()
$dirty=-not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain | Out-String))
$result=[ordered]@{Commit=$revision;WorktreeDirty=$dirty;CompletedAtUtc=[DateTime]::UtcNow.ToString('O');Security=$security;Failover=$failover;CrashRestartDataPreserved=$true;SameCountCorruptionRejected=$true;Result='PASS_LOCAL_DATABASE_FAULTS';FullUpgrade014Accepted=$false}
$result | ConvertTo-Json -Depth 8 | Set-Content "$evidenceRoot/local-recovery.json"
$result | ConvertTo-Json -Depth 8

param(
    [ValidateSet('Initialize','Start','Stop','Crash','Use','Status')][string]$Action = 'Status',
    [string]$EnvironmentRoot = 'D:\Program Files\ET\_Env'
)
$ErrorActionPreference = 'Stop'
$localRoot = Join-Path $EnvironmentRoot 'mongo-local'
$server = (Get-ChildItem "$EnvironmentRoot\server" -Filter mongod.exe -Recurse | Select-Object -First 1).FullName
$shell = (Get-ChildItem "$EnvironmentRoot\shell" -Filter mongosh.exe -Recurse | Select-Object -First 1).FullName
$dump = (Get-ChildItem "$EnvironmentRoot\tools" -Filter mongodump.exe -Recurse | Select-Object -First 1).FullName
if (-not $server -or -not $shell -or -not $dump) { throw 'Run Install-LocalMongo.ps1 first' }
$env:PATH = "$(Split-Path $server);$(Split-Path $shell);$(Split-Path $dump);$env:PATH"
if ($Action -eq 'Initialize') {
    if (Test-Path "$localRoot\secrets.json") { throw 'Already initialized. Use Start instead.' }
    New-Item -ItemType Directory -Force -Path $localRoot | Out-Null
    # Restrict inherited access before generating credentials and private keys.
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    & icacls.exe $localRoot /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot secure environment ACL' }
    $caKey = [System.Security.Cryptography.RSA]::Create(3072)
    $hash = [System.Security.Cryptography.HashAlgorithmName]::SHA256
    $padding = [System.Security.Cryptography.RSASignaturePadding]::Pkcs1
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=ET Local Test CA', $caKey, $hash, $padding)
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
    $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign,$true))
    $ca = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddYears(2))
    $ca.ExportCertificatePem() | Set-Content "$localRoot\ca.pem" -Encoding ascii
    # The CA private key is ephemeral and never saved.
    $key = [System.Security.Cryptography.RSA]::Create(3072)
    $leafRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost,O=ET Local', $key, $hash, $padding)
    $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $san.AddDnsName('localhost'); $san.AddIpAddress([System.Net.IPAddress]::Loopback)
    $leafRequest.CertificateExtensions.Add($san.Build())
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false,$false,0,$true))
    $usages = [System.Security.Cryptography.OidCollection]::new()
    $usages.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
    $usages.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.2')) | Out-Null
    $leafRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages,$false))
    $leaf = $leafRequest.Create($ca,[DateTimeOffset]::Now.AddDays(-1),[DateTimeOffset]::Now.AddYears(1),[System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
    ($leaf.ExportCertificatePem() + "`n" + $key.ExportPkcs8PrivateKeyPem()) | Set-Content "$localRoot\server.pem" -Encoding ascii
    [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(512)) | Set-Content "$localRoot\replica.key" -Encoding ascii
    @{ Password=[Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) } | ConvertTo-Json | Set-Content "$localRoot\secrets.json"
    foreach ($port in 27101..27103) {
        New-Item -ItemType Directory -Force -Path "$localRoot\$port" | Out-Null
        $yamlRoot = $localRoot.Replace('\','/')
        @"
storage:
  dbPath: '$yamlRoot/$port'
  wiredTiger:
    engineConfig:
      cacheSizeGB: 0.25
systemLog:
  destination: file
  path: '$yamlRoot/$port/mongod.log'
  logAppend: true
net:
  bindIp: '127.0.0.1,::1'
  ipv6: true
  port: $port
  tls:
    mode: requireTLS
    certificateKeyFile: '$yamlRoot/server.pem'
    CAFile: '$yamlRoot/ca.pem'
    allowConnectionsWithoutCertificates: true
replication:
  replSetName: et-local-rs
security:
  keyFile: '$yamlRoot/replica.key'
  authorization: enabled
"@ | Set-Content "$localRoot\$port.yml"
    }
    $key.Dispose(); $caKey.Dispose(); $leaf.Dispose(); $ca.Dispose()
}
if ($Action -in 'Start','Initialize') {
    foreach ($port in 27101..27103) {
        $existing = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue
        if ($existing) {
            $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($existing[0].OwningProcess)"
            if ($owner.ExecutablePath -ne $server -or $owner.CommandLine -notlike "*$localRoot\$port.yml*") { throw "Port $port is occupied by another process" }
            continue
        }
        Start-Process -FilePath $server -ArgumentList "--config `"$localRoot\$port.yml`"" -WindowStyle Hidden | Out-Null
    }
}
if ($Action -eq 'Crash') {
    # Target only the exact installed binary and this environment's config files.
    foreach ($process in Get-CimInstance Win32_Process -Filter "Name='mongod.exe'") {
        if ($process.ExecutablePath -eq $server -and $process.CommandLine -like "*$localRoot\27*.yml*") {
            Stop-Process -Id $process.ProcessId
        }
    }
    Write-Host 'Local test nodes force-stopped for crash recovery test; data retained.'
    return
}
$env:ET_LOCAL_MONGO_ROOT = $localRoot
$env:ET_MONGO_CA_FILE = "$localRoot\ca.pem"
$password = (Get-Content "$localRoot\secrets.json" -Raw | ConvertFrom-Json).Password
$env:ET_MONGO_URI = "mongodb://etlocal:$password@localhost:27101,localhost:27102,localhost:27103/?authSource=admin&tls=true&replicaSet=et-local-rs&retryWrites=true"
$env:ET_MONGO_DATABASE = 'ET_LOCAL_ACCEPTANCE'
if ($Action -eq 'Stop') {
    & $shell --nodb --quiet --eval 'for (const port of [27103,27102,27101]) { try { const uri=process.env.ET_MONGO_URI.replace("localhost:27101,localhost:27102,localhost:27103", "localhost:"+port).replace("replicaSet=et-local-rs", "directConnection=true") + "&serverSelectionTimeoutMS=2000&tlsCAFile=" + encodeURIComponent(process.env.ET_MONGO_CA_FILE); new Mongo(uri).getDB("admin").runCommand({shutdown:1,force:true}); } catch(e) { if (!/network|closed|ECONNREFUSED|ECONNRESET|connection|timed out/i.test(e.message)) throw e; } }'
    if ($LASTEXITCODE -ne 0) { throw 'Graceful node shutdown failed' }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        $remaining = @(Get-CimInstance Win32_Process -Filter "Name='mongod.exe'" | Where-Object { $_.ExecutablePath -eq $server -and $_.CommandLine -like "*$localRoot\27*.yml*" })
        if ($remaining.Count -eq 0) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Local nodes did not stop within 30 seconds' }
        Start-Sleep -Milliseconds 200
    } while ($true)
    Write-Host 'Local test nodes shut down gracefully; data retained.'
    return
}
if ($Action -eq 'Initialize') {
    & $shell --nodb --quiet --file "$PSScriptRoot\Initialize-LocalMongo.js"
    if ($LASTEXITCODE -ne 0) { throw 'Replica set initialization failed' }
}
if ($Action -in 'Status','Start') {
    & $shell --nodb --quiet --eval 'const c = new Mongo(process.env.ET_MONGO_URI + "&serverSelectionTimeoutMS=30000&tlsCAFile=" + encodeURIComponent(process.env.ET_MONGO_CA_FILE)); printjson(c.getDB("admin").runCommand({replSetGetStatus:1}).members.map(m=>({name:m.name,state:m.stateStr})));'
    if ($LASTEXITCODE -ne 0) { throw 'Local MongoDB status failed' }
}
Write-Host "Local MongoDB environment loaded: $localRoot (credentials not displayed)"

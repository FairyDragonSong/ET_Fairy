param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$TrustedPublicKeyFile,
    [ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedSigningKeyId,
    [switch]$RequireCleanProvenance,
    [switch]$RuntimeSmoke,
    [switch]$AllowUnsignedTestArtifact
)

$ErrorActionPreference='Stop'
$release=[IO.Path]::GetFullPath($ReleaseDirectory)
if(-not(Test-Path -LiteralPath $release -PathType Container)){throw 'Release directory does not exist'}
$archives=@(Get-ChildItem -LiteralPath $release -File -Filter 'ET.Server-*.zip')
if($archives.Count -ne 1){throw 'Release must contain exactly one server archive'}
$archive=$archives[0]
$checksumPath="$($archive.FullName).sha256"
if(-not(Test-Path -LiteralPath $checksumPath -PathType Leaf)){throw 'Detached archive checksum is missing'}
$signed=-not [string]::IsNullOrWhiteSpace($TrustedPublicKeyFile)
if($signed -ne (-not [string]::IsNullOrWhiteSpace($ExpectedSigningKeyId))){throw 'Trusted public key and expected key id must be provided together'}
if(-not $signed -and -not $AllowUnsignedTestArtifact){throw 'Release verification requires a trusted public key'}
$releaseFiles=@(Get-ChildItem -LiteralPath $release -File)
$expectedReleaseFileCount=$(if($signed){4}else{2})
if($releaseFiles.Count -ne $expectedReleaseFileCount){throw 'Release directory contains an unexpected number of files'}
$checksum=(Get-Content -LiteralPath $checksumPath -Raw).Trim()
$expectedName=[regex]::Escape($archive.Name)
if($checksum -notmatch "^(?<hash>[a-fA-F0-9]{64})  $expectedName$"){throw 'Detached checksum format or archive name is invalid'}
$actual=(Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if($actual -ne $Matches.hash.ToLowerInvariant()){throw 'Release archive checksum mismatch'}
if($signed){
    $publicKeyPath=[IO.Path]::GetFullPath($TrustedPublicKeyFile)
    if(-not(Test-Path -LiteralPath $publicKeyPath -PathType Leaf)){throw 'Trusted public key file does not exist'}
    $attestationPath="$($archive.FullName).attestation.json"
    $signaturePath="$($archive.FullName).attestation.sig"
    if(-not(Test-Path -LiteralPath $attestationPath -PathType Leaf) -or -not(Test-Path -LiteralPath $signaturePath -PathType Leaf)){
        throw 'Signed release attestation is incomplete'
    }
    $attestationBytes=[IO.File]::ReadAllBytes($attestationPath)
    $attestation=[Text.Encoding]::UTF8.GetString($attestationBytes)|ConvertFrom-Json
    $archiveVersionMatch=[regex]::Match($archive.Name,'^ET\.Server-(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)\.zip$')
    $signedAt=[DateTimeOffset]::MinValue
    if($attestation.SchemaVersion -ne 1 -or $attestation.Product -ne 'ET.Server' -or
        -not $archiveVersionMatch.Success -or $attestation.Version -ne $archiveVersionMatch.Groups['version'].Value -or
        $attestation.ArchiveFile -ne $archive.Name -or $attestation.ArchiveSha256 -ne $actual -or
        $attestation.SourceCommit -notmatch '^[a-f0-9]{40}$' -or $attestation.SigningKeyId -ne $ExpectedSigningKeyId -or
        $attestation.SignatureAlgorithm -ne 'RSASSA-PSS-SHA256' -or $attestation.ManifestSha256 -notmatch '^[a-f0-9]{64}$' -or
        -not [DateTimeOffset]::TryParse([string]$attestation.SignedAtUtc,[ref]$signedAt) -or $signedAt -gt [DateTimeOffset]::UtcNow.AddMinutes(5)){
        throw 'Signed release attestation fields are invalid'
    }
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $publicPem=[IO.File]::ReadAllText($publicKeyPath)
        if($publicPem -match 'PRIVATE KEY'){throw 'Verifier requires a public key file, not a private key'}
        $rsa.ImportFromPem($publicPem)
        if($rsa.KeySize -lt 3072){throw 'Trusted RSA key must be at least 3072 bits'}
        $signature=[IO.File]::ReadAllBytes($signaturePath)
        if(-not $rsa.VerifyData($attestationBytes,$signature,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)){throw 'Release signature is not trusted'}
    }
    finally{$rsa.Dispose()}
}

$extract=Join-Path $env:TEMP ('et-release-verify-'+[guid]::NewGuid().ToString('N'))
try{
    Expand-Archive -LiteralPath $archive.FullName -DestinationPath $extract
    $manifestPath=Join-Path $extract 'manifest.json'
    if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Release manifest is missing'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 10
    if($manifest.SchemaVersion -ne 1 -or $manifest.Product -ne 'ET.Server' -or $manifest.SourceCommit -notmatch '^[a-f0-9]{40}$'){
        throw 'Release manifest identity is invalid'
    }
    if($RequireCleanProvenance -and $manifest.WorktreeDirty){throw 'Dirty source provenance is not accepted'}
    if($signed){
        $manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if($manifestHash -ne $attestation.ManifestSha256 -or $manifest.SourceCommit -ne $attestation.SourceCommit -or
            $manifest.Version -ne $attestation.Version){throw 'Signed attestation does not match archive manifest'}
    }
    $expected=@{}
    foreach($entry in @($manifest.Files)){
        if($entry.Path -notmatch '^(Bin|Config)/' -or $entry.Path -match '(^|/)\.\.(/|$)' -or $expected.ContainsKey($entry.Path)){
            throw 'Manifest contains an unsafe or duplicate path'
        }
        $expected[$entry.Path]=$entry
    }
    $actualFiles=@(Get-ChildItem -LiteralPath $extract -File -Recurse|Where-Object{$_.FullName -ne $manifestPath})
    if($actualFiles.Count -ne $expected.Count){throw 'Release file count does not match manifest'}
    foreach($file in $actualFiles){
        $relative=$file.FullName.Substring($extract.Length+1).Replace('\','/')
        if(-not $expected.ContainsKey($relative)){throw "Unlisted release file: $relative"}
        $entry=$expected[$relative]
        if([long]$entry.Bytes -ne $file.Length){throw "Release file size mismatch: $relative"}
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if($hash -ne ([string]$entry.Sha256).ToLowerInvariant()){throw "Release file hash mismatch: $relative"}
    }
    foreach($required in @('Bin/App.exe','Bin/App.dll','Bin/App.runtimeconfig.json','Bin/Hotfix.dll','Bin/Hotfix.pdb',
        'Config/NLog/NLog.config','Config/NLog/NLog.Bounded.config')){
        if(-not $expected.ContainsKey($required)){throw "Required manifest file missing: $required"}
    }
    $smokePassed=$false
    if($RuntimeSmoke){
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=Join-Path $extract 'Bin/App.exe'
        $start.WorkingDirectory=Join-Path $extract 'Bin'
        $start.Arguments='--Process=8 --StartConfig=StartConfig/Release --Console=1 --ShutdownTimeoutSeconds=5'
        $start.UseShellExecute=$false;$start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
        $start.Environment['ET_LOG_PROFILE']='bounded'
        $process=[Diagnostics.Process]::new();$process.StartInfo=$start
        try{
            if(-not $process.Start()){throw 'Release smoke process did not start'}
            $deadline=[DateTime]::UtcNow.AddSeconds(15);$live=$null
            while([DateTime]::UtcNow -lt $deadline){
                try{$live=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:30300/health/live' -TimeoutSec 1;break}
                catch{Start-Sleep -Milliseconds 200}
            }
            if($null -eq $live -or $live.StatusCode -ne 200){throw 'Release liveness smoke failed'}
            $process.StandardInput.WriteLine('shutdown')
            if(-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0){throw 'Release graceful shutdown smoke failed'}
            $smokePassed=$true
        }
        finally{
            if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
            $process.Dispose()
        }
    }
    [ordered]@{Passed=$true;Archive=$archive.FullName;Sha256=$actual;Version=$manifest.Version;
        SourceCommit=$manifest.SourceCommit;WorktreeDirty=[bool]$manifest.WorktreeDirty;Files=$actualFiles.Count;
        Signed=$signed;SigningKeyId=$(if($signed){$attestation.SigningKeyId}else{$null});
        RuntimeSmoke=$(if($RuntimeSmoke){$smokePassed}else{$null})}|ConvertTo-Json
}
finally{
    if(Test-Path -LiteralPath $extract){Remove-Item -LiteralPath $extract -Recurse -Force}
}

$script:BackupRestoreUtf8=[Text.UTF8Encoding]::new($false)

function Assert-BackupPathOutsideRepository([string]$Path,[string]$Name){
    $resolved=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\','/')){throw "$Name cannot be a drive root"}
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if(($resolved+[IO.Path]::DirectorySeparatorChar).StartsWith($repository,[StringComparison]::OrdinalIgnoreCase)){
        throw "$Name must be outside the source repository"
    }
    return $resolved
}

function Resolve-BackupExternalKey([string]$Path,[string]$Name,[bool]$RequirePrivate){
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not(Test-Path -LiteralPath $resolved -PathType Leaf)){throw "$Name key does not exist"}
    $repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if($RequirePrivate -and $resolved.StartsWith($repository,[StringComparison]::OrdinalIgnoreCase)){
        throw "$Name private key must be outside the repository"
    }
    $pem=[IO.File]::ReadAllText($resolved)
    if($RequirePrivate -and $pem -notmatch 'PRIVATE KEY'){throw "$Name requires a private key"}
    if(-not $RequirePrivate -and $pem -match 'PRIVATE KEY'){throw "$Name verifier requires a public key"}
    return $resolved
}

function Get-BackupKeyFingerprint([string]$Path,[string]$Name,[bool]$RequirePrivate){
    $resolved=Resolve-BackupExternalKey $Path $Name $RequirePrivate
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($resolved))
        if($rsa.KeySize -lt 3072){throw "$Name key must be at least RSA-3072"}
        if($RequirePrivate){$rsa.ExportParameters($true)|Out-Null}
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}

function Test-BackupSignature([byte[]]$Bytes,[byte[]]$Signature,[string]$PublicKeyFile,[string]$Name){
    $path=Resolve-BackupExternalKey $PublicKeyFile $Name $false
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($path))
        if($rsa.KeySize -lt 3072){throw "$Name key must be at least RSA-3072"}
        if(-not $rsa.VerifyData($Bytes,$Signature,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)){throw "$Name signature is not trusted"}
    }
    finally{$rsa.Dispose()}
}

function New-BackupSignature([byte[]]$Bytes,[string]$PrivateKeyFile,[string]$Name){
    $path=Resolve-BackupExternalKey $PrivateKeyFile $Name $true
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText($path))
        if($rsa.KeySize -lt 3072){throw "$Name key must be at least RSA-3072"}
        return $rsa.SignData($Bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally{$rsa.Dispose()}
}

function Start-RestoreAuthorizationConsumption([string]$LedgerDirectory,[string]$OperationId,[string]$AuthorizationFile){
    $root=Assert-BackupPathOutsideRepository $LedgerDirectory 'OperationLedgerDirectory'
    New-Item -ItemType Directory -Path $root -Force|Out-Null
    $ledger=Join-Path $root "$OperationId.restore-operation.json"
    $entry=[ordered]@{SchemaVersion=1;Product='ET.Server';OperationId=$OperationId;Status='Started';
        AuthorizationSha256=(Get-FileHash -LiteralPath $AuthorizationFile -Algorithm SHA256).Hash.ToLowerInvariant();
        StartedAtUtc=[DateTime]::UtcNow.ToString('O')}
    $bytes=$script:BackupRestoreUtf8.GetBytes(($entry|ConvertTo-Json))
    $stream=$null
    try{
        $stream=[IO.File]::Open($ledger,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)
    }catch [IO.IOException]{throw 'Restore authorization operation was already consumed or is in progress'}
    finally{if($stream){$stream.Dispose()}}
    return $ledger
}

function Complete-RestoreAuthorizationConsumption([string]$LedgerFile,[bool]$Succeeded){
    $entry=Get-Content -LiteralPath $LedgerFile -Raw|ConvertFrom-Json -DateKind String
    $entry.Status=$(if($Succeeded){'Completed'}else{'Failed'})
    $entry|Add-Member -NotePropertyName CompletedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O'))
    [IO.File]::WriteAllText($LedgerFile,($entry|ConvertTo-Json),$script:BackupRestoreUtf8)
}

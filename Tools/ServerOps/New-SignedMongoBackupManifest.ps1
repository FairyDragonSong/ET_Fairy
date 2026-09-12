param(
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$SourceDatabase,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$BackupId,
    [Parameter(Mandatory)][string]$SigningPrivateKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$SigningKeyId
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"
$root=Assert-BackupPathOutsideRepository $BackupPath 'BackupPath'
if(-not(Test-Path -LiteralPath $root -PathType Container)){throw 'BackupPath does not exist'}
if(-not(Test-Path -LiteralPath (Join-Path $root $SourceDatabase) -PathType Container)){throw 'Source database dump directory is missing'}
$manifestPath=Join-Path $root 'backup-manifest.json';$signaturePath=$manifestPath+'.sig'
if((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $signaturePath)){
    throw 'Signed backup manifest already exists; refusing to overwrite'
}
$files=[Collections.Generic.List[object]]::new()
foreach($item in @(Get-ChildItem -LiteralPath $root -Force -Recurse|Sort-Object FullName)){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Backup cannot contain reparse points'}
    if($item.PSIsContainer){continue}
    $relative=$item.FullName.Substring($root.Length+1).Replace('\','/')
    if($relative -in @('backup-manifest.json','backup-manifest.json.sig') -or $relative -match '(^|/)\.\.(/|$)'){
        throw 'Backup contains a reserved or unsafe file path'
    }
    if(-not $relative.StartsWith($SourceDatabase+'/',[StringComparison]::Ordinal)){
        throw 'Database-scoped backup contains a file outside the source database directory'
    }
    $files.Add([ordered]@{Path=$relative;Bytes=[int64]$item.Length;
        Sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
}
if($files.Count -lt 1){throw 'Backup contains no dump files'}
$privatePath=Resolve-BackupExternalKey $SigningPrivateKeyFile 'Backup signing' $true
if(($privatePath+[IO.Path]::DirectorySeparatorChar).StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Backup signing private key cannot be stored inside the backup artifact'
}
$manifest=[ordered]@{SchemaVersion=2;Product='ET.Server';ArtifactType='MongoBackup';BackupId=$BackupId;
    SourceDatabase=$SourceDatabase;CreatedAtUtc=[DateTime]::UtcNow.ToString('O');SigningKeyId=$SigningKeyId;
    SigningPublicKeySha256=(Get-BackupKeyFingerprint $SigningPrivateKeyFile 'Backup signing' $true);Files=$files}
$bytes=$script:BackupRestoreUtf8.GetBytes(($manifest|ConvertTo-Json -Depth 8))
$signature=New-BackupSignature $bytes $SigningPrivateKeyFile 'Backup signing'
try{
    [IO.File]::WriteAllBytes($manifestPath,$bytes);[IO.File]::WriteAllBytes($signaturePath,$signature)
    [ordered]@{Passed=$true;BackupId=$BackupId;Manifest=$manifestPath;ManifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        Files=$files.Count;Bytes=[int64](($files|Measure-Object Bytes -Sum).Sum)}|ConvertTo-Json
}catch{
    if(Test-Path -LiteralPath $manifestPath){Remove-Item -LiteralPath $manifestPath -Force}
    if(Test-Path -LiteralPath $signaturePath){Remove-Item -LiteralPath $signaturePath -Force}
    throw
}

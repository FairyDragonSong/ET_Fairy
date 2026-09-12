param(
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$TrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedSigningKeyId,
    [string]$ExpectedSourceDatabase
)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/BackupRestore.Common.ps1"
$root=Assert-BackupPathOutsideRepository $BackupPath 'BackupPath'
$manifestPath=Join-Path $root 'backup-manifest.json';$signaturePath=$manifestPath+'.sig'
if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf) -or -not(Test-Path -LiteralPath $signaturePath -PathType Leaf)){
    throw 'Signed backup manifest or signature is missing'
}
$bytes=[IO.File]::ReadAllBytes($manifestPath)
Test-BackupSignature $bytes ([IO.File]::ReadAllBytes($signaturePath)) $TrustedPublicKeyFile 'Backup'
try{$manifest=$script:BackupRestoreUtf8.GetString($bytes)|ConvertFrom-Json -Depth 10 -DateKind String}catch{throw 'Backup manifest JSON is invalid'}
$fingerprint=Get-BackupKeyFingerprint $TrustedPublicKeyFile 'Backup' $false
$created=[DateTimeOffset]::MinValue
if($manifest.SchemaVersion -ne 2 -or $manifest.Product -ne 'ET.Server' -or $manifest.ArtifactType -ne 'MongoBackup' -or
   $manifest.BackupId -notmatch '^[0-9A-Za-z._-]{3,80}$' -or $manifest.SourceDatabase -notmatch '^[A-Za-z0-9_-]+$' -or
   $manifest.SigningKeyId -cne $ExpectedSigningKeyId -or $manifest.SigningPublicKeySha256 -cne $fingerprint -or
   -not[DateTimeOffset]::TryParse([string]$manifest.CreatedAtUtc,[ref]$created) -or $created -gt [DateTimeOffset]::UtcNow.AddMinutes(5) -or
   ($ExpectedSourceDatabase -and $manifest.SourceDatabase -cne $ExpectedSourceDatabase)){
    throw 'Backup manifest identity is invalid'
}
$expected=@{}
foreach($entry in @($manifest.Files)){
    if($entry.Path -notmatch '^[^\\/:*?"<>|]+(?:/[^\\/:*?"<>|]+)*$' -or $entry.Path -match '(^|/)\.\.(/|$)' -or
       $entry.Path -in @('backup-manifest.json','backup-manifest.json.sig') -or $expected.ContainsKey([string]$entry.Path) -or
       [int64]$entry.Bytes -lt 0 -or $entry.Sha256 -notmatch '^[a-f0-9]{64}$'){throw 'Backup manifest file entry is invalid'}
    if(-not([string]$entry.Path).StartsWith(([string]$manifest.SourceDatabase)+'/',[StringComparison]::Ordinal)){
        throw 'Backup manifest contains a file outside the source database directory'
    }
    $expected[[string]$entry.Path]=$entry
}
$actual=@(Get-ChildItem -LiteralPath $root -Force -Recurse|Where-Object{
    -not $_.PSIsContainer -and $_.FullName -ne $manifestPath -and $_.FullName -ne $signaturePath})
if($actual.Count -ne $expected.Count){throw 'Backup file count differs from signed manifest'}
foreach($file in $actual){
    if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Backup contains a reparse point'}
    $relative=$file.FullName.Substring($root.Length+1).Replace('\','/')
    if(-not $expected.ContainsKey($relative)){throw "Backup contains an unlisted file: $relative"}
    $entry=$expected[$relative]
    if($file.Length -ne [int64]$entry.Bytes -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Sha256){
        throw "Backup file integrity failed: $relative"
    }
}
if(-not(Test-Path -LiteralPath (Join-Path $root $manifest.SourceDatabase) -PathType Container)){throw 'Signed source database directory is missing'}
[ordered]@{Passed=$true;BackupId=$manifest.BackupId;SourceDatabase=$manifest.SourceDatabase;
    ManifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();Files=$actual.Count;
    Bytes=[int64](($actual|Measure-Object Length -Sum).Sum);CreatedAtUtc=$manifest.CreatedAtUtc}|ConvertTo-Json

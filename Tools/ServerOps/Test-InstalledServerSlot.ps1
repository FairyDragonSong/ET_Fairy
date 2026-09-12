param(
    [Parameter(Mandatory)][string]$SlotDirectory,
    [Parameter(Mandatory)][string]$TrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [ValidatePattern('^[0-9A-Za-z.-]+-[a-f0-9]{12}$')][string]$ExpectedSlotId,
    [switch]$AllowDirtyTestArtifact
)

$ErrorActionPreference='Stop'
$slot=[IO.Path]::GetFullPath($SlotDirectory)
if(-not(Test-Path -LiteralPath $slot -PathType Container)){throw 'Installed server slot does not exist'}
$evidence=Join-Path $slot 'evidence'
$package=Join-Path $slot 'package'
$metadataPath=Join-Path $slot 'slot-metadata.json'
if(-not(Test-Path -LiteralPath $evidence -PathType Container) -or
   -not(Test-Path -LiteralPath $package -PathType Container) -or
   -not(Test-Path -LiteralPath $metadataPath -PathType Leaf)){
    throw 'Installed server slot structure is incomplete'
}
$releaseParameters=@{ReleaseDirectory=$evidence;TrustedPublicKeyFile=$TrustedPublicKeyFile;
    ExpectedSigningKeyId=$ExpectedSigningKeyId;ExpectedPackageSupplier=$ExpectedPackageSupplier;
    ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase}
if(-not $AllowDirtyTestArtifact){$releaseParameters.RequireCleanProvenance=$true}
$raw=& "$PSScriptRoot/Test-ServerRelease.ps1" @releaseParameters
try{$release=($raw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12}
catch{throw 'Release verifier did not return valid JSON'}
$metadata=Get-Content -LiteralPath $metadataPath -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$expectedSlotId="$($release.Version)-$($release.Sha256.Substring(0,12))"
if($ExpectedSlotId -and $ExpectedSlotId -ne $expectedSlotId){throw 'Caller expected a different installed slot id'}
if($metadata.SchemaVersion -ne 1 -or $metadata.Product -ne 'ET.Server' -or $metadata.SlotId -ne $expectedSlotId -or
   $metadata.Version -ne $release.Version -or $metadata.SourceCommit -ne $release.SourceCommit -or
   $metadata.ArchiveSha256 -ne $release.Sha256 -or
   (-not $ExpectedSlotId -and [IO.Path]::GetFileName($slot) -ne $expectedSlotId)){
    throw 'Installed server slot metadata does not match signed release'
}
$manifestPath=Join-Path $package '_manifest/et/release-manifest.json'
if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Installed release manifest is missing'}
$manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$attestationFile=@(Get-ChildItem -LiteralPath $evidence -File -Filter '*.attestation.json')
if($attestationFile.Count -ne 1){throw 'Installed release attestation is ambiguous'}
$attestation=Get-Content -LiteralPath $attestationFile[0].FullName -Raw|ConvertFrom-Json
if($manifestHash -ne $attestation.ManifestSha256){throw 'Installed manifest differs from signed release'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json -Depth 12
$expected=@{}
foreach($entry in @($manifest.Files)){
    if($entry.Path -notmatch '^(Bin|Config|_manifest/spdx_2\.2)/' -or $entry.Path -match '(^|/)\.\.(/|$)' -or
       $expected.ContainsKey([string]$entry.Path)){throw 'Installed manifest contains an unsafe or duplicate path'}
    $expected[[string]$entry.Path]=$entry
}
$actual=@(Get-ChildItem -LiteralPath $package -File -Recurse|Where-Object{$_.FullName -ne $manifestPath})
if($actual.Count -ne $expected.Count){throw 'Installed server slot file count differs from signed manifest'}
foreach($file in $actual){
    $relative=$file.FullName.Substring($package.Length+1).Replace('\','/')
    if(-not $expected.ContainsKey($relative)){throw "Installed server slot contains an unlisted file: $relative"}
    $entry=$expected[$relative]
    if([long]$entry.Bytes -ne $file.Length -or
       ([string]$entry.Sha256).ToLowerInvariant() -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()){
        throw "Installed server slot file integrity failed: $relative"
    }
}
[ordered]@{Passed=$true;SlotId=$metadata.SlotId;Version=$release.Version;SourceCommit=$release.SourceCommit;
    ArchiveSha256=$release.Sha256;Files=$actual.Count;WorktreeDirty=[bool]$release.WorktreeDirty;
    InstalledAtUtc=$metadata.InstalledAtUtc}|ConvertTo-Json

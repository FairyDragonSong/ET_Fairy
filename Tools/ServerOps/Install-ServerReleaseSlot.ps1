param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][string]$DeploymentRoot,
    [Parameter(Mandatory)][string]$TrustedPublicKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ExpectedSigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$ExpectedPackageSupplier,
    [Parameter(Mandatory)][uri]$ExpectedSbomNamespaceBase,
    [Parameter(Mandatory)][string]$ConfirmDeploymentRoot,
    [switch]$AllowDirtyTestArtifact
)

$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=[IO.Path]::GetFullPath($DeploymentRoot).TrimEnd('\','/')
if($ConfirmDeploymentRoot -cne $root){throw 'ConfirmDeploymentRoot must exactly match the resolved deployment root'}
if($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\','/')){throw 'DeploymentRoot cannot be a drive root'}
$repoPrefix=$repositoryRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(($root+[IO.Path]::DirectorySeparatorChar).StartsWith($repoPrefix,[StringComparison]::OrdinalIgnoreCase)){
    throw 'DeploymentRoot must be outside the source repository'
}
$releaseParameters=@{ReleaseDirectory=$ReleaseDirectory;TrustedPublicKeyFile=$TrustedPublicKeyFile;
    ExpectedSigningKeyId=$ExpectedSigningKeyId;ExpectedPackageSupplier=$ExpectedPackageSupplier;
    ExpectedSbomNamespaceBase=$ExpectedSbomNamespaceBase}
if(-not $AllowDirtyTestArtifact){$releaseParameters.RequireCleanProvenance=$true}
$raw=& "$PSScriptRoot/Test-ServerRelease.ps1" @releaseParameters
try{$release=($raw -join [Environment]::NewLine)|ConvertFrom-Json -Depth 12}
catch{throw 'Release verifier did not return valid JSON'}
$slotId="$($release.Version)-$($release.Sha256.Substring(0,12))"
$releasesRoot=Join-Path $root 'releases'
New-Item -ItemType Directory -Path $releasesRoot -Force|Out-Null
$final=Join-Path $releasesRoot $slotId
if(Test-Path -LiteralPath $final){throw 'Immutable release slot already exists; refusing to overwrite'}
$staging=Join-Path $releasesRoot ('.staging-'+[guid]::NewGuid().ToString('N'))
try{
    $evidence=Join-Path $staging 'evidence';$package=Join-Path $staging 'package'
    New-Item -ItemType Directory -Path $evidence|Out-Null
    New-Item -ItemType Directory -Path $package|Out-Null
    foreach($file in @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($ReleaseDirectory)) -File)){
        Copy-Item -LiteralPath $file.FullName -Destination $evidence
    }
    $archives=@(Get-ChildItem -LiteralPath $evidence -File -Filter 'ET.Server-*.zip')
    if($archives.Count -ne 1){throw 'Copied release evidence is incomplete'}
    if((Get-FileHash -LiteralPath $archives[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $release.Sha256){
        throw 'Release archive changed after verification'
    }
    Expand-Archive -LiteralPath $archives[0].FullName -DestinationPath $package
    $metadata=[ordered]@{SchemaVersion=1;Product='ET.Server';SlotId=$slotId;Version=$release.Version;
        SourceCommit=$release.SourceCommit;ArchiveSha256=$release.Sha256;InstalledAtUtc=[DateTime]::UtcNow.ToString('O')}
    [IO.File]::WriteAllText((Join-Path $staging 'slot-metadata.json'),($metadata|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    & "$PSScriptRoot/Test-InstalledServerSlot.ps1" -SlotDirectory $staging -TrustedPublicKeyFile $TrustedPublicKeyFile `
        -ExpectedSigningKeyId $ExpectedSigningKeyId -ExpectedPackageSupplier $ExpectedPackageSupplier `
        -ExpectedSbomNamespaceBase $ExpectedSbomNamespaceBase -ExpectedSlotId $slotId `
        -AllowDirtyTestArtifact:$AllowDirtyTestArtifact|Out-Null
    Move-Item -LiteralPath $staging -Destination $final
    [ordered]@{Passed=$true;SlotId=$slotId;SlotDirectory=$final;Version=$release.Version;
        SourceCommit=$release.SourceCommit;ArchiveSha256=$release.Sha256;WorktreeDirty=[bool]$release.WorktreeDirty}|ConvertTo-Json
}
finally{
    $stagingPrefix=$releasesRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar+'.staging-'
    if((Test-Path -LiteralPath $staging) -and $staging.StartsWith($stagingPrefix,[StringComparison]::OrdinalIgnoreCase)){
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

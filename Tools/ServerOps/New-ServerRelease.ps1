param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputRoot=(Join-Path $PSScriptRoot '../../Publish/Server'),
    [string]$SigningPrivateKeyFile,
    [ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$SigningKeyId,
    [Parameter(Mandatory)][ValidatePattern('^[^:\r\n]{2,120}$')][string]$PackageSupplier,
    [Parameter(Mandatory)][uri]$SbomNamespaceBase,
    [ValidateSet('protocol-identical-only','reviewed-backward-compatible')][string]$RollingCompatibility='protocol-identical-only',
    [ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$CompatibilityReviewId,
    [switch]$AllowDirtyTestArtifact,
    [switch]$AllowUnsignedTestArtifact
)

$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedOutput=[IO.Path]::GetFullPath($OutputRoot)
if($resolvedOutput.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($resolvedOutput).TrimEnd('\','/')){throw 'OutputRoot cannot be a drive root'}
$commit=(& git -C $repositoryRoot rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$'){throw 'Source commit is unavailable'}
$dirty=@(& git -C $repositoryRoot status --porcelain).Count -ne 0
if($dirty -and -not $AllowDirtyTestArtifact){throw 'Release creation requires a clean worktree'}
if(-not $SbomNamespaceBase.IsAbsoluteUri -or $SbomNamespaceBase.Scheme -ne 'https'){throw 'SBOM namespace base must be an absolute HTTPS URI'}
if($RollingCompatibility -eq 'reviewed-backward-compatible' -and [string]::IsNullOrWhiteSpace($CompatibilityReviewId)){
    throw 'Reviewed rolling compatibility requires an explicit review id'
}
if($RollingCompatibility -eq 'protocol-identical-only' -and -not [string]::IsNullOrWhiteSpace($CompatibilityReviewId)){
    throw 'CompatibilityReviewId is only valid for reviewed rolling compatibility'
}
$signed=-not [string]::IsNullOrWhiteSpace($SigningPrivateKeyFile)
if($signed -ne (-not [string]::IsNullOrWhiteSpace($SigningKeyId))){throw 'Signing key file and key id must be provided together'}
if(-not $signed -and -not $AllowUnsignedTestArtifact){throw 'Release creation requires an external signing key'}
$privateKeyPath=$null
if($signed){
    $privateKeyPath=[IO.Path]::GetFullPath($SigningPrivateKeyFile)
    if(-not(Test-Path -LiteralPath $privateKeyPath -PathType Leaf)){throw 'Signing private key file does not exist'}
    $repositoryPrefix=$repositoryRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if($privateKeyPath.StartsWith($repositoryPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Signing private key must not be stored inside the repository'}
    $rsaCheck=[Security.Cryptography.RSA]::Create()
    try{
        $rsaCheck.ImportFromPem([IO.File]::ReadAllText($privateKeyPath))
        if($rsaCheck.KeySize -lt 3072){throw 'Signing RSA key must be at least 3072 bits'}
        $rsaCheck.ExportParameters($true)|Out-Null
    }
    finally{$rsaCheck.Dispose()}
}

$migrationSource=Get-Content -LiteralPath (Join-Path $repositoryRoot 'Unity/Assets/Scripts/Hotfix/Server/Demo/Persistence/DatabaseMigration.cs') -Raw
# 从迁移源码读取协议常量，避免脚本重复硬编码。
function Read-MigrationConstant([string]$Name){
    $match=[regex]::Match($migrationSource,"public\s+const\s+int\s+$Name\s*=\s*(?<value>[0-9]+)\s*;")
    if(-not $match.Success){throw "Database migration compatibility constant is missing: $Name"}
    return [int]$match.Groups['value'].Value
}
$databaseMinimum=Read-MigrationConstant 'MinimumReadableVersion'
$databaseMaximum=Read-MigrationConstant 'MaximumReadableVersion'
$databaseTarget=Read-MigrationConstant 'TargetVersion'
if($databaseMinimum -gt $databaseTarget -or $databaseTarget -gt $databaseMaximum){throw 'Database compatibility range does not include its target'}
$protocolHash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
try{
    $protoRoot=Join-Path $repositoryRoot 'Unity/Assets/Config/Proto'
    $protoFiles=@(Get-ChildItem -LiteralPath $protoRoot -File -Filter '*.proto' -Recurse|Sort-Object FullName)
    if($protoFiles.Count -eq 0){throw 'No protocol source files found'}
    foreach($proto in $protoFiles){
        $relative=$proto.FullName.Substring($protoRoot.Length+1).Replace('\','/')
        $protocolHash.AppendData([Text.Encoding]::UTF8.GetBytes($relative))
        $protocolHash.AppendData([byte[]]@(0))
        $protocolHash.AppendData([IO.File]::ReadAllBytes($proto.FullName))
    }
    $protocolSha256=[Convert]::ToHexString($protocolHash.GetHashAndReset()).ToLowerInvariant()
}
finally{$protocolHash.Dispose()}

$final=Join-Path $resolvedOutput $Version
if(Test-Path -LiteralPath $final){throw 'Release version already exists; refusing to overwrite'}
New-Item -ItemType Directory -Path $resolvedOutput -Force|Out-Null
$staging=Join-Path $resolvedOutput ('.staging-'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $staging 'package'
$bin=Join-Path $package 'Bin'
try{
    New-Item -ItemType Directory -Path $bin -Force|Out-Null
    & dotnet restore (Join-Path $repositoryRoot 'DotNet/DotNet.sln') --locked-mode
    if($LASTEXITCODE -ne 0){throw 'Locked restore failed'}
    & dotnet publish (Join-Path $repositoryRoot 'DotNet/App/DotNet.App.csproj') -c Release --no-restore -o $bin
    if($LASTEXITCODE -ne 0){throw 'App publish failed'}
    & dotnet build (Join-Path $repositoryRoot 'DotNet/Hotfix/DotNet.Hotfix.csproj') -c Release --no-restore "-p:OutputPath=$bin\"
    if($LASTEXITCODE -ne 0){throw 'Hotfix build failed'}
    foreach($required in @('App.exe','App.dll','App.runtimeconfig.json','Hotfix.dll','Hotfix.pdb')){
        if(-not(Test-Path -LiteralPath (Join-Path $bin $required) -PathType Leaf)){throw "Required server file missing: $required"}
    }
    $config=Join-Path $package 'Config'
    New-Item -ItemType Directory -Path (Join-Path $config 'NLog') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Config/NLog/NLog.config') -Destination (Join-Path $config 'NLog/NLog.config')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Config/NLog/NLog.Bounded.config') -Destination (Join-Path $config 'NLog/NLog.Bounded.config')
    New-Item -ItemType Directory -Path (Join-Path $config 'Excel') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Config/Excel/s') -Destination (Join-Path $config 'Excel/s') -Recurse
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Config/Recast') -Destination (Join-Path $config 'Recast') -Recurse

    $toolManifest=Join-Path $repositoryRoot '.config/dotnet-tools.json'
    & dotnet tool restore --tool-manifest $toolManifest
    if($LASTEXITCODE -ne 0){throw 'Pinned SBOM tool restore failed'}
    $sbomTimestamp=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $validationOutput=Join-Path $staging 'sbom-validation.json'
    Push-Location $repositoryRoot
    try{
        & dotnet tool run sbom-tool -- Generate -b $package -bc (Join-Path $repositoryRoot 'DotNet') -pn ET.Server -pv $Version `
            -ps $PackageSupplier -nsb $SbomNamespaceBase.AbsoluteUri.TrimEnd('/') -nsu "$commit-$Version" -gt $sbomTimestamp `
            -F false -pm true -li false -mi SPDX:2.2 -V Warning
        if($LASTEXITCODE -ne 0){throw 'SPDX SBOM generation failed'}
        & dotnet tool run sbom-tool -- Validate -b $package -o $validationOutput -mi SPDX:2.2 -F false -n -V Warning
        if($LASTEXITCODE -ne 0){throw 'SPDX SBOM validation failed'}
    }
    finally{Pop-Location}
    $validation=Get-Content -LiteralPath $validationOutput -Raw|ConvertFrom-Json -Depth 12
    if($validation.Result -ne 'Success' -or $validation.ValidationErrors.Count -ne 0 -or
        $validation.Summary.ValidationTelemetery.TotalPackagesInManifest -lt 1){throw 'SPDX SBOM validation report is incomplete'}
    Remove-Item -LiteralPath $validationOutput -Force
    $spdxPath=Join-Path $package '_manifest/spdx_2.2/manifest.spdx.json'
    if(-not(Test-Path -LiteralPath $spdxPath -PathType Leaf)){throw 'SPDX 2.2 manifest is missing'}

    $files=@(Get-ChildItem -LiteralPath $package -File -Recurse|Sort-Object FullName|ForEach-Object{
        [ordered]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');Bytes=$_.Length;
            Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $compatibility=[ordered]@{SchemaVersion=1;DatabaseMinimumReadable=$databaseMinimum;
        DatabaseMaximumReadable=$databaseMaximum;DatabaseTarget=$databaseTarget;ProtocolSha256=$protocolSha256;
        RollingMode=$RollingCompatibility;ReviewId=$(if($CompatibilityReviewId){$CompatibilityReviewId}else{$null})}
    $manifest=[ordered]@{SchemaVersion=2;Product='ET.Server';Version=$Version;SourceCommit=$commit;
        WorktreeDirty=$dirty;CreatedAtUtc=[DateTime]::UtcNow.ToString('O');Platform='windows-x64';SelfContained=$false;
        RequiredRuntime='Microsoft.NETCore.App 8.0';StartWorkingDirectory='Bin';Compatibility=$compatibility;Files=$files}
    $utf8=[Text.UTF8Encoding]::new($false)
    $manifestPath=Join-Path $package '_manifest/et/release-manifest.json'
    New-Item -ItemType Directory -Path (Split-Path $manifestPath -Parent) -Force|Out-Null
    [IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 6),$utf8)
    $manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $archiveName="ET.Server-$Version.zip"
    $archive=Join-Path $staging $archiveName
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -CompressionLevel Optimal
    $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $staging "$archiveName.sha256"),"$archiveHash  $archiveName`n",$utf8)
    if($signed){
        $attestation=[ordered]@{SchemaVersion=1;Product='ET.Server';Version=$Version;SourceCommit=$commit;
            ArchiveFile=$archiveName;ArchiveSha256=$archiveHash;ManifestSha256=$manifestHash;SigningKeyId=$SigningKeyId;
            SignatureAlgorithm='RSASSA-PSS-SHA256';SignedAtUtc=[DateTime]::UtcNow.ToString('O')}
        $attestationBytes=$utf8.GetBytes(($attestation|ConvertTo-Json -Depth 4))
        $attestationPath=Join-Path $staging "$archiveName.attestation.json"
        [IO.File]::WriteAllBytes($attestationPath,$attestationBytes)
        $rsa=[Security.Cryptography.RSA]::Create()
        try{
            $rsa.ImportFromPem([IO.File]::ReadAllText($privateKeyPath))
            if($rsa.KeySize -lt 3072){throw 'Signing RSA key must be at least 3072 bits'}
            $signature=$rsa.SignData($attestationBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pss)
            [IO.File]::WriteAllBytes((Join-Path $staging "$archiveName.attestation.sig"),$signature)
        }
        finally{$rsa.Dispose()}
    }
    Remove-Item -LiteralPath $package -Recurse -Force
    Move-Item -LiteralPath $staging -Destination $final
    [ordered]@{Passed=$true;Version=$Version;SourceCommit=$commit;WorktreeDirty=$dirty;Files=$files.Count;
        Archive=(Join-Path $final $archiveName);Sha256=$archiveHash;Signed=$signed;SigningKeyId=$SigningKeyId;
        Compatibility=$compatibility}|ConvertTo-Json -Depth 5
}
finally{
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
}

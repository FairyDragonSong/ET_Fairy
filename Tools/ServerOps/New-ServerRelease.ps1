param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputRoot=(Join-Path $PSScriptRoot '../../Publish/Server'),
    [switch]$AllowDirtyTestArtifact
)

$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$resolvedOutput=[IO.Path]::GetFullPath($OutputRoot)
if($resolvedOutput.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($resolvedOutput).TrimEnd('\','/')){throw 'OutputRoot cannot be a drive root'}
$commit=(& git -C $repositoryRoot rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$'){throw 'Source commit is unavailable'}
$dirty=@(& git -C $repositoryRoot status --porcelain).Count -ne 0
if($dirty -and -not $AllowDirtyTestArtifact){throw 'Release creation requires a clean worktree'}

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

    $files=@(Get-ChildItem -LiteralPath $package -File -Recurse|Sort-Object FullName|ForEach-Object{
        [ordered]@{Path=$_.FullName.Substring($package.Length+1).Replace('\','/');Bytes=$_.Length;
            Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $manifest=[ordered]@{SchemaVersion=1;Product='ET.Server';Version=$Version;SourceCommit=$commit;
        WorktreeDirty=$dirty;CreatedAtUtc=[DateTime]::UtcNow.ToString('O');Platform='windows-x64';SelfContained=$false;
        RequiredRuntime='Microsoft.NETCore.App 8.0';StartWorkingDirectory='Bin';Files=$files}
    [IO.File]::WriteAllText((Join-Path $package 'manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))

    $archiveName="ET.Server-$Version.zip"
    $archive=Join-Path $staging $archiveName
    Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -CompressionLevel Optimal
    $archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $staging "$archiveName.sha256"),"$archiveHash  $archiveName`n",[Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $package -Recurse -Force
    Move-Item -LiteralPath $staging -Destination $final
    [ordered]@{Passed=$true;Version=$Version;SourceCommit=$commit;WorktreeDirty=$dirty;Files=$files.Count;
        Archive=(Join-Path $final $archiveName);Sha256=$archiveHash}|ConvertTo-Json
}
finally{
    if(Test-Path -LiteralPath $staging){Remove-Item -LiteralPath $staging -Recurse -Force}
}

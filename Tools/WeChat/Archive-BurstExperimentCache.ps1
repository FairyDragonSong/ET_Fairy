$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$unityRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'Unity')) + [IO.Path]::DirectorySeparatorChar
$archiveRoot = [IO.Path]::GetFullPath((Join-Path $workspace ('Temp/WeChatCpuExperiments/cache-archive/' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))))
$allowedArchive = [IO.Path]::GetFullPath((Join-Path $workspace 'Temp/WeChatCpuExperiments/cache-archive')) + [IO.Path]::DirectorySeparatorChar
if (!$archiveRoot.StartsWith($allowedArchive, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive outside intended directory' }
foreach ($relative in @('Unity/Temp/BurstOutput','Unity/Library/BurstCache/Wasm-Wasm')) {
    $sourcePath = [IO.Path]::GetFullPath((Join-Path $workspace $relative))
    if (!$sourcePath.StartsWith($unityRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cache outside Unity workspace' }
    if (!(Test-Path -LiteralPath $sourcePath)) { continue }
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    $destinationPath = [IO.Path]::GetFullPath((Join-Path $archiveRoot ([IO.Path]::GetFileName($sourcePath))))
    if (!$destinationPath.StartsWith($allowedArchive, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected move target' }
    Move-Item -LiteralPath $sourcePath -Destination $destinationPath
    Write-Output ("Archived {0} -> {1}" -f $sourcePath, $destinationPath)
}

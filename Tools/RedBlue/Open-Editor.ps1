param(
    [string]$EditorPath = 'D:\Program Files\TuanJieEdit\2022.3.62t11\Editor\Tuanjie.exe'
)

$ErrorActionPreference = 'Stop'
$projectPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Unity'))
if (!(Test-Path -LiteralPath $EditorPath -PathType Leaf)) { throw "Editor not found: $EditorPath" }
if (!(Test-Path -LiteralPath (Join-Path $projectPath 'Assets') -PathType Container)) { throw "Invalid project: $projectPath" }

# The app's child process environment can omit this standard Windows variable.
# TPM joins it with Tuanjie/config while reading its global configuration.
if ([string]::IsNullOrWhiteSpace($env:ALLUSERSPROFILE))
{
    $commonData = [Environment]::GetFolderPath('CommonApplicationData')
    if ([string]::IsNullOrWhiteSpace($commonData)) { throw 'Cannot locate CommonApplicationData' }
    $env:ALLUSERSPROFILE = $commonData
}
Start-Process -FilePath $EditorPath -ArgumentList @('-projectPath', ('"' + $projectPath + '"')) -WorkingDirectory $projectPath

param(
    [ValidateSet('All', 'Assets', 'Entry')][string]$Mode = 'All',
    [string]$Editor = 'D:/Program Files/TuanJieEdit/2022.3.62t11/Editor/Tuanjie.exe'
)
$ErrorActionPreference = 'Stop'
$pingDouRepo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$pingDouTemp = Join-Path $pingDouRepo 'Temp'
New-Item -ItemType Directory -Force $pingDouTemp | Out-Null
if ($Mode -eq 'All') {
    & dotnet run --project (Join-Path $PSScriptRoot 'PingDou.Tests.csproj')
    if ($LASTEXITCODE -ne 0) { throw 'PingDou core checks failed.' }
}
function Invoke-PingDouEditor([string]$Method, [string]$LogName) {
    $pingDouLog = Join-Path $pingDouTemp $LogName
    $pingDouStart = [System.Diagnostics.ProcessStartInfo]::new()
    $pingDouStart.FileName = $Editor
    $pingDouStart.UseShellExecute = $false
    $pingDouStart.CreateNoWindow = $true
    $pingDouStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    # Tuanjie UPM requires this standard environment entry in the editor child process.
    $pingDouStart.Environment['ALLUSERSPROFILE'] = [Environment]::GetFolderPath('CommonApplicationData')
    $pingDouStart.ArgumentList.Add('-batchmode')
    $pingDouStart.ArgumentList.Add('-projectPath')
    $pingDouStart.ArgumentList.Add((Join-Path $pingDouRepo 'Unity'))
    $pingDouStart.ArgumentList.Add('-executeMethod')
    $pingDouStart.ArgumentList.Add($Method)
    $pingDouStart.ArgumentList.Add('-logFile')
    $pingDouStart.ArgumentList.Add($pingDouLog)
    $pingDouProcess = [System.Diagnostics.Process]::Start($pingDouStart)
    $pingDouProcess.WaitForExit()
    if ($pingDouProcess.ExitCode -ne 0) { throw "Editor validation failed: $pingDouLog" }
    Write-Host "Passed: $Method ($pingDouLog)"
}
if ($Mode -in @('All', 'Assets')) { Invoke-PingDouEditor 'ET.PingDouEditor.BuildAndVerify' 'PingDou-editor.log' }
if ($Mode -in @('All', 'Entry')) { Invoke-PingDouEditor 'ET.PingDouEntrySmoke.Run' 'PingDou-entry.log' }

param([string]$LogRoot=(Join-Path $PSScriptRoot '../../Logs'),
    [ValidateRange(0.001,100000)][double]$BudgetGiB=10,
    [ValidateRange(0,100000)][double]$MinimumFreeGiB=5)
$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($LogRoot)
if($resolved.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\','/')){throw 'Specify a log directory, not a drive root'}
if(-not(Test-Path -LiteralPath $resolved -PathType Container)){throw 'Log directory does not exist'}
$files=@(Get-ChildItem -LiteralPath $resolved -File -Recurse -Force)
$bytes=[long](($files|Measure-Object Length -Sum).Sum)
$free=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($resolved))).AvailableFreeSpace
$over=$bytes -gt ($BudgetGiB*1GB)
$low=$free -lt ($MinimumFreeGiB*1GB)
[ordered]@{ObservedAtUtc=[DateTime]::UtcNow.ToString('O');LogRoot=$resolved;FileCount=$files.Count;Bytes=$bytes;
    BudgetBytes=[long]($BudgetGiB*1GB);DriveFreeBytes=$free;OverBudget=$over;BelowFreeFloor=$low;ReadOnly=$true}|ConvertTo-Json
if($over -or $low){exit 1}

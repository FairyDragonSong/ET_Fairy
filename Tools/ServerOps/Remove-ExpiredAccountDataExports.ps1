param(
    [Parameter(Mandatory)][string]$ExportRoot,
    [Parameter(Mandatory)][string]$ConfirmExportRoot,
    [Parameter(Mandatory)][ValidateRange(1,365)][int]$RetentionDays,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
if(-not[IO.Path]::IsPathFullyQualified($ExportRoot)){throw 'Export root must be absolute'}
$root=[IO.Path]::GetFullPath($ExportRoot).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
$confirmation=[IO.Path]::GetFullPath($ConfirmExportRoot).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
$comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
if(-not $root.Equals($confirmation,$comparison)){throw 'Export root confirmation does not match'}
if($root-eq[IO.Path]::GetPathRoot($root).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)-or-not(Test-Path -LiteralPath $root -PathType Container)){throw 'Export root is unsafe'}
$rootItem=Get-Item -LiteralPath $root -Force
if(($rootItem.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw 'Export root cannot be a reparse point'}
$cutoff=[DateTime]::UtcNow.AddDays(-$RetentionDays)
$all=@(Get-ChildItem -LiteralPath $root -File -Force)
if($all.Count-gt 10000){throw 'Export root file count exceeds the cleanup limit'}
$candidates=[Collections.Generic.List[IO.FileInfo]]::new()
foreach($file in $all){
    if($file.Name-notmatch'^(?<operation>[a-f0-9]{32})\.json$'-or$file.LastWriteTimeUtc-ge$cutoff){continue}
    if(($file.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne 0){throw "Expired export cannot be a reparse point: $($file.Name)"}
    if($file.Length-le 0-or$file.Length-gt 16MB){throw "Expired export size is invalid: $($file.Name)"}
    $document=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -Depth 20 -DateKind String
    if($document.SchemaVersion-ne 1-or$document.OperationId-cne$Matches.operation-or[long]$document.AccountId-le 0-or[string]::IsNullOrWhiteSpace([string]$document.GeneratedAtUtc)){
        throw "Expired export identity is invalid: $($file.Name)"
    }
    $candidates.Add($file)
}
$removed=0
if($Apply){
    foreach($file in $candidates){[IO.File]::Delete($file.FullName);if([IO.File]::Exists($file.FullName)){throw "Expired export deletion failed: $($file.Name)"};$removed++}
}
[ordered]@{Passed=$true;Mode=$(if($Apply){'apply'}else{'audit'});ExportRoot=$root;RetentionDays=$RetentionDays;CutoffUtc=$cutoff.ToString('O');CandidateCount=$candidates.Count;RemovedCount=$removed;CandidateNames=@($candidates.Name)}|ConvertTo-Json -Depth 4

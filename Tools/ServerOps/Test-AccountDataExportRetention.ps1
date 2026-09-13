$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-account-export-retention-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
function Write-Export([string]$operation,[int]$ageDays,[switch]$Invalid){
    $path=Join-Path $fixture "$operation.json"
    $document=[ordered]@{SchemaVersion=$(if($Invalid){2}else{1});OperationId=$operation;AccountId=123;GeneratedAtUtc=[DateTime]::UtcNow.AddDays(-$ageDays).ToString('O');Account=@{};Roles=@();Sessions=@();StateAudits=@()}
    [IO.File]::WriteAllText($path,($document|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false));(Get-Item -LiteralPath $path).LastWriteTimeUtc=[DateTime]::UtcNow.AddDays(-$ageDays);return $path
}
try{
    $old=Write-Export ([Guid]::NewGuid().ToString('N')) 10;$fresh=Write-Export ([Guid]::NewGuid().ToString('N')) 1;$invalid=Write-Export ([Guid]::NewGuid().ToString('N')) 10 -Invalid
    $wrongConfirmation=$false;try{& "$PSScriptRoot/Remove-ExpiredAccountDataExports.ps1" -ExportRoot $fixture -ConfirmExportRoot (Join-Path $fixture 'wrong') -RetentionDays 7|Out-Null}catch{$wrongConfirmation=$true};if(-not$wrongConfirmation){throw 'Wrong cleanup root confirmation accepted'}
    $invalidRejected=$false;try{& "$PSScriptRoot/Remove-ExpiredAccountDataExports.ps1" -ExportRoot $fixture -ConfirmExportRoot $fixture -RetentionDays 7 -Apply|Out-Null}catch{$invalidRejected=$true};if(-not$invalidRejected-or-not(Test-Path -LiteralPath $old)){throw 'Invalid export did not fail closed before deletion'}
    [IO.File]::Delete($invalid)
    $audit=(((& "$PSScriptRoot/Remove-ExpiredAccountDataExports.ps1" -ExportRoot $fixture -ConfirmExportRoot $fixture -RetentionDays 7)-join[Environment]::NewLine)|ConvertFrom-Json)
    if($audit.Mode-ne'audit'-or$audit.CandidateCount-ne 1-or$audit.RemovedCount-ne 0-or-not(Test-Path -LiteralPath $old)){throw 'Retention audit changed files or selected wrong candidates'}
    $apply=(((& "$PSScriptRoot/Remove-ExpiredAccountDataExports.ps1" -ExportRoot $fixture -ConfirmExportRoot $fixture -RetentionDays 7 -Apply)-join[Environment]::NewLine)|ConvertFrom-Json)
    if($apply.Mode-ne'apply'-or$apply.RemovedCount-ne 1-or(Test-Path -LiteralPath $old)-or-not(Test-Path -LiteralPath $fresh)){throw 'Retention apply did not remove exactly the expired export'}
    Write-Host 'PASS account export retention audit, fail-closed validation and exact expiry deletion'
}finally{
    if(Test-Path -LiteralPath $fixture){[IO.Directory]::Delete($fixture,$true)}
}

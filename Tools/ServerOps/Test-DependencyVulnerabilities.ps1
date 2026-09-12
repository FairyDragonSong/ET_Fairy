param([string]$Solution=(Join-Path $PSScriptRoot '../../DotNet/DotNet.sln'))

$ErrorActionPreference='Stop'
$resolved=[IO.Path]::GetFullPath($Solution)
if(-not(Test-Path -LiteralPath $resolved -PathType Leaf)){throw 'Solution does not exist'}

$output=& dotnet list $resolved package --vulnerable --include-transitive --format json 2>&1
if($LASTEXITCODE -ne 0){throw 'NuGet vulnerability query failed closed'}
try{$report=($output -join [Environment]::NewLine)|ConvertFrom-Json -Depth 20}
catch{throw 'NuGet vulnerability output was not valid JSON'}
if($report.version -ne 1 -or $null -eq $report.projects){throw 'Unsupported NuGet vulnerability report'}

$findings=[Collections.Generic.List[object]]::new()
foreach($project in $report.projects){
    foreach($framework in @($project.frameworks)){
        $packages=[Collections.Generic.List[object]]::new()
        if($null -ne $framework.topLevelPackages){$packages.AddRange([object[]]@($framework.topLevelPackages))}
        if($null -ne $framework.transitivePackages){$packages.AddRange([object[]]@($framework.transitivePackages))}
        foreach($package in $packages){
            if($null -eq $package.vulnerabilities){continue}
            foreach($vulnerability in @($package.vulnerabilities)){
                $findings.Add([ordered]@{Project=$project.path;Framework=$framework.framework;Package=$package.id;
                    Version=$package.resolvedVersion;Severity=$vulnerability.severity;Advisory=$vulnerability.advisoryurl})
            }
        }
    }
}
$result=[ordered]@{ObservedAtUtc=[DateTime]::UtcNow.ToString('O');Solution=$resolved;
    Projects=@($report.projects).Count;Findings=$findings.Count;Passed=($findings.Count -eq 0)}
$result|ConvertTo-Json -Depth 5
if($findings.Count -ne 0){
    [Console]::Error.WriteLine(($findings|ConvertTo-Json -Depth 4))
    exit 1
}

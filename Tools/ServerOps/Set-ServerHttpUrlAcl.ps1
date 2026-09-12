param(
    [ValidateSet('Audit','Apply')][string]$Action='Audit',
    [ValidateRange(1024,65535)][int]$Port=30300,
    [string]$BindIp='127.0.0.1',
    [ValidatePattern('^/(?:[A-Za-z0-9._-]+/)*$')][string]$Path='/',
    [string]$ServiceIdentity,
    [string]$ConfirmUrl
)
$ErrorActionPreference='Stop'
$parsedIp=$null
if(-not [Net.IPAddress]::TryParse($BindIp,[ref]$parsedIp) -or -not [Net.IPAddress]::IsLoopback($parsedIp)){
    throw 'Server HTTP URL ACL must use an explicit loopback IP'
}
$target="http://${BindIp}:$Port$Path"
$wildcards=@("http://*:$Port$Path","http://+:$Port$Path")
$output=(& netsh http show urlacl)
if($LASTEXITCODE -ne 0){throw 'Cannot inspect Windows HTTP URL ACLs'}
$hasTarget=[bool]($output -match [regex]::Escape($target))
$presentWildcards=@($wildcards|Where-Object{$output -match [regex]::Escape($_)})
if($Action -eq 'Audit'){
    [ordered]@{
        Passed=$hasTarget-and$presentWildcards.Count-eq0
        Target=$target
        ExactReservation=$hasTarget
        WildcardReservations=$presentWildcards
        RequiresElevation=$true
    }|ConvertTo-Json
    if(-not $hasTarget -or $presentWildcards.Count-ne0){exit 2}
    exit 0
}
if($ConfirmUrl -cne $target){throw 'ConfirmUrl must exactly match the loopback URL being applied'}
if([string]::IsNullOrWhiteSpace($ServiceIdentity) -or $ServiceIdentity.Length -gt 256 -or $ServiceIdentity -match '[\r\n]'){
    throw 'Apply requires the explicit Windows service identity'
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Apply must run in an elevated PowerShell'
}
if(-not $hasTarget){
    & netsh http add urlacl url=$target user="$ServiceIdentity" listen=yes|Out-Null
    if($LASTEXITCODE -ne 0){
        throw 'Exact URL ACL creation failed; existing wildcard reservations were not changed'
    }
}
foreach($wildcard in $presentWildcards){
    & netsh http delete urlacl url=$wildcard|Out-Null
    if($LASTEXITCODE -ne 0){
        throw "Wildcard URL ACL removal failed; the exact reservation remains available: $wildcard"
    }
}
Write-Output "Server HTTP URL ACL restricted to $target for $ServiceIdentity"

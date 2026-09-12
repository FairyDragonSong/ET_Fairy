param([string]$EnvironmentRoot = 'D:\Program Files\ET\_Env')
$ErrorActionPreference = 'Stop'
$packages = @(
    @{ Name='server'; Url='https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.32.zip'; Hash='5a0675fdec49b544cd5d374ad50a9a594cf5b3278b5ace658aa51cbb4df17177' },
    @{ Name='shell'; Url='https://github.com/mongodb-js/mongosh/releases/download/v2.10.0/mongosh-2.10.0-win32-x64.zip'; Hash='4f52b0bb5a08374b8ceec19d2b8da7544985aab377d5799642f31a198554562a' },
    @{ Name='tools'; Url='https://fastdl.mongodb.org/tools/db/mongodb-database-tools-windows-x86_64-100.18.0.zip'; Hash='feb29eed9788b1da2fb6f7ae63909ac790a6fe3a363947e68590f048bed08edb' }
)
New-Item -ItemType Directory -Force -Path "$EnvironmentRoot\downloads" | Out-Null
foreach ($package in $packages) {
    $zip = "$EnvironmentRoot\downloads\$($package.Name).zip"
    if (-not (Test-Path -LiteralPath $zip)) {
        Write-Host "Downloading $($package.Name)"
        Invoke-WebRequest $package.Url -OutFile $zip
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $package.Hash) { throw 'Package SHA256 mismatch' }
    $destination = "$EnvironmentRoot\$($package.Name)"
    if (-not (Test-Path -LiteralPath $destination)) { Expand-Archive -LiteralPath $zip -DestinationPath $destination }
    Write-Host "Verified and installed $($package.Name)"
}
$packages | ConvertTo-Json | Set-Content "$EnvironmentRoot\packages.json"

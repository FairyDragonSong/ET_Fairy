function Invoke-MongoTool {
    param([string]$Tool, [string[]]$Arguments)
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('et-mongo-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    try {
        if ($IsWindows) {
            $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            & icacls.exe $tempRoot /inheritance:r /grant:r "${identity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Unable to secure temporary tool config' }
        } else { & chmod 700 $tempRoot; if ($LASTEXITCODE -ne 0) { throw 'Unable to secure temporary tool config' } }
        # Database Tools supports sensitive URI/password values in a YAML config.
        $config = Join-Path $tempRoot 'connection.yml'
        ('uri: ' + ($env:ET_MONGO_URI | ConvertTo-Json -Compress)) | Set-Content -LiteralPath $config
        $toolArguments = @("--config=$config") + $Arguments
        if ($env:ET_MONGO_CA_FILE) { $toolArguments += "--sslCAFile=$env:ET_MONGO_CA_FILE" }
        & $Tool @toolArguments
        if ($LASTEXITCODE -ne 0) { throw "$Tool failed with exit code $LASTEXITCODE" }
    } finally {
        # Only our known single file and newly-created empty directory are removed.
        if (Test-Path -LiteralPath (Join-Path $tempRoot 'connection.yml')) { Remove-Item -LiteralPath (Join-Path $tempRoot 'connection.yml') }
        Remove-Item -LiteralPath $tempRoot
    }
}

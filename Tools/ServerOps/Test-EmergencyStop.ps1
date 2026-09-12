param([switch]$SkipBuild)

$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-emergency-stop-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
$utf8=[Text.UTF8Encoding]::new($false)
$processes=[Collections.Generic.List[Diagnostics.Process]]::new()
try{
    if(-not $SkipBuild){
        & dotnet build (Join-Path $repositoryRoot 'DotNet/DotNet.sln') -c Release --no-restore
        if($LASTEXITCODE -ne 0){throw 'Emergency stop test build failed'}
    }
    $rsa=[Security.Cryptography.RSA]::Create(3072)
    try{
        $private=Join-Path $fixture 'stop.private.pem'
        $public=Join-Path $fixture 'stop.public.pem'
        [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8)
        [IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8)
        $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $trust=Join-Path $fixture 'stop-trust.json'
        ConvertTo-Json -InputObject @([ordered]@{KeyId='emergency-test';PublicKeyPem=$rsa.ExportSubjectPublicKeyInfoPem();
            DeploymentId='local-emergency-test';NotBefore=$now-60;NotAfter=$now+3600}) -Depth 4|
            Set-Content -LiteralPath $trust -Encoding utf8NoBOM
    }
    finally{$rsa.Dispose()}

    function New-Stop([string]$Path){
        & "$PSScriptRoot/New-EmergencyStopCommand.ps1" -PrivateKeyFile $private -KeyId emergency-test `
            -DeploymentId local-emergency-test -ReasonCode automated-test -ValidMinutes 10 `
            -ConfirmDeploymentId local-emergency-test -OutputFile $Path|Out-Null
    }
    function Start-App([string]$CommandPath,[string]$Deployment='local-emergency-test',[bool]$IncludeAll=$true,[int]$Develop=1){
        $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $repositoryRoot 'Bin/App.exe'))
        $info.WorkingDirectory=Join-Path $repositoryRoot 'Bin'
        $info.Arguments="--Process=8 --StartConfig=StartConfig/Release --Console=0 --Develop=$Develop --ShutdownTimeoutSeconds=5"
        $info.UseShellExecute=$false;$info.CreateNoWindow=$true
        $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
        foreach($name in @('ET_EMERGENCY_STOP_FILE','ET_EMERGENCY_STOP_TRUST_FILE','ET_DEPLOYMENT_ID','ET_SECRET_ROOT')){$info.Environment.Remove($name)|Out-Null}
        $info.Environment['ET_EMERGENCY_STOP_FILE']=$CommandPath
        if($IncludeAll){$info.Environment['ET_EMERGENCY_STOP_TRUST_FILE']=$trust;$info.Environment['ET_DEPLOYMENT_ID']=$Deployment}
        if($Develop -eq 0){$info.Environment['ET_SECRET_ROOT']=$fixture}
        $process=[Diagnostics.Process]::new();$process.StartInfo=$info
        if(-not $process.Start()){throw 'Emergency stop test process failed to start'}
        $processes.Add($process);return $process
    }
    function Wait-Exit([Diagnostics.Process]$Process,[int]$Expected,[string]$Case){
        $stderr=$Process.StandardError.ReadToEndAsync();$stdout=$Process.StandardOutput.ReadToEndAsync()
        if(-not $Process.WaitForExit(15000)){throw "$Case process did not exit"}
        [Threading.Tasks.Task]::WaitAll(@($stderr,$stdout),5000)|Out-Null
        if($Process.ExitCode -ne $Expected){throw "$Case exit=$($Process.ExitCode), expected=$Expected; stderr=$($stderr.Result)"}
    }

    $startupCommand=Join-Path $fixture 'startup-stop.json';New-Stop $startupCommand
    Wait-Exit (Start-App $startupCommand 'local-emergency-test' $true 0) 4 'valid-production-startup-stop'

    $tamperedCommand=Join-Path $fixture 'tampered-stop.json';Copy-Item -LiteralPath $startupCommand -Destination $tamperedCommand
    $tampered=Get-Content -LiteralPath $tamperedCommand -Raw|ConvertFrom-Json
    $signature=[Convert]::FromBase64String($tampered.Signature);$signature[0]=$signature[0]-bxor1
    $tampered.Signature=[Convert]::ToBase64String($signature)
    $tampered|ConvertTo-Json|Set-Content -LiteralPath $tamperedCommand -Encoding utf8NoBOM
    Wait-Exit (Start-App $tamperedCommand) 3 'tampered-startup-stop'
    Wait-Exit (Start-App $startupCommand 'another-deployment') 3 'cross-deployment-stop'
    Wait-Exit (Start-App (Join-Path $fixture 'missing.json') 'local-emergency-test' $false) 3 'partial-configuration'

    $runtimeCommand=Join-Path $fixture 'runtime-stop.json'
    $runtime=Start-App $runtimeCommand
    $deadline=[DateTime]::UtcNow.AddSeconds(15);$live=$null
    while([DateTime]::UtcNow-lt$deadline){
        try{$live=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:30300/health/live' -TimeoutSec 1;break}
        catch{Start-Sleep -Milliseconds 200}
    }
    if($null-eq$live-or$live.StatusCode-ne200){throw 'Runtime emergency test did not become live'}
    New-Stop $runtimeCommand
    Wait-Exit $runtime 4 'runtime-graceful-stop'
    if(Get-NetTCPConnection -LocalPort 30300 -State Listen -ErrorAction SilentlyContinue){throw 'Emergency-stopped process retained RouterManager port'}

    [ordered]@{Passed=$true;ValidStartupStopExit=4;TamperedStopExit=3;CrossDeploymentExit=3;
        PartialConfigurationExit=3;RuntimeGracefulStopExit=4;ListenerReleased=$true}|ConvertTo-Json
}
finally{
    foreach($process in $processes){
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -and
       [IO.Path]::GetFileName($resolved) -match '^et-emergency-stop-[a-f0-9]{32}$' -and
       (Test-Path -LiteralPath $resolved)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

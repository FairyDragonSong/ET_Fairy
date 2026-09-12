param(
    [Parameter(Mandatory)][string]$PrivateKeyFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$KeyId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,80}$')][string]$DeploymentId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Za-z._-]{3,64}$')][string]$ReasonCode,
    [ValidatePattern('^[a-f0-9]{32}$')][string]$OperationId=[guid]::NewGuid().ToString('N'),
    [ValidateRange(5,1440)][int]$ValidMinutes=60,
    [Parameter(Mandatory)][string]$ConfirmDeploymentId,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference='Stop'
if($ConfirmDeploymentId -cne $DeploymentId){throw 'ConfirmDeploymentId must exactly match DeploymentId'}
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$private=[IO.Path]::GetFullPath($PrivateKeyFile)
$output=[IO.Path]::GetFullPath($OutputFile)
$repoPrefix=$repositoryRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
if(-not(Test-Path -LiteralPath $private -PathType Leaf)){throw 'Emergency stop private key does not exist'}
if($private.StartsWith($repoPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Emergency stop private key must be outside the repository'}
if($output.StartsWith($repoPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Emergency stop command must be outside the repository'}
$parent=Split-Path $output -Parent
if(-not(Test-Path -LiteralPath $parent -PathType Container)){throw 'Emergency stop output directory does not exist'}
if(Test-Path -LiteralPath $output){throw 'Emergency stop output already exists; refusing to overwrite'}
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem([IO.File]::ReadAllText($private))
    if($rsa.KeySize -lt 3072){throw 'Emergency stop signing key must be at least RSA-3072'}
    $rsa.ExportParameters($true)|Out-Null
    $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $payload=[ordered]@{SchemaVersion=1;Action='stop';DeploymentId=$DeploymentId;OperationId=$OperationId;
        ReasonCode=$ReasonCode;IssuedAt=$now;ExpiresAt=$now+60*$ValidMinutes}
    $utf8=[Text.UTF8Encoding]::new($false)
    $payloadBytes=$utf8.GetBytes(($payload|ConvertTo-Json -Compress))
    $signature=$rsa.SignData($payloadBytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope=[ordered]@{SchemaVersion=1;KeyId=$KeyId;Payload=[Convert]::ToBase64String($payloadBytes);
        Signature=[Convert]::ToBase64String($signature)}
    $bytes=$utf8.GetBytes(($envelope|ConvertTo-Json))
    $temporary=Join-Path $parent ('.emergency-stop-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try{
        [IO.File]::WriteAllBytes($temporary,$bytes)
        Move-Item -LiteralPath $temporary -Destination $output
    }
    finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
    [ordered]@{Passed=$true;Output=$output;DeploymentId=$DeploymentId;OperationId=$OperationId;
        KeyId=$KeyId;ReasonCode=$ReasonCode;ExpiresAt=$payload.ExpiresAt;
        Sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}
finally{$rsa.Dispose()}

param(
    [Parameter(Mandatory)][string]$PrivateKeyFile,
    [Parameter(Mandatory)][string]$OperatorId,
    [Parameter(Mandatory)][string]$DeploymentId,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$Zone,
    [Parameter(Mandatory)][ValidateSet('enable','disable')][string]$Action,
    [Parameter(Mandatory)][long]$AccountId,
    [Parameter(Mandatory)][long]$ConfirmAccountId,
    [Parameter(Mandatory)][long]$ExpectedVersion,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')][string]$ReasonCode,
    [Parameter(Mandatory)][string]$OutputFile,
    [string]$OperationId=([Guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference='Stop'
if($AccountId -le 0 -or $ConfirmAccountId -ne $AccountId -or $ExpectedVersion -lt 0 -or $ExpectedVersion -eq [long]::MaxValue){throw 'Account confirmation or version invalid'}
if($OperationId -notmatch '^[a-fA-F0-9]{32}$'){throw 'OperationId must be a GUID in N format'}
if(Test-Path -LiteralPath $OutputFile){throw 'Output already exists; keep the previous command for retry'}
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PrivateKeyFile)))
    if($rsa.KeySize -lt 3072){throw 'RSA key must be at least 3072 bits'}
    $issued=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $command=[ordered]@{OperationId=$OperationId;OperatorId=$OperatorId;Action=$Action;Database=$Database;DeploymentId=$DeploymentId;Zone=$Zone;AccountId=$AccountId;ConfirmAccountId=$ConfirmAccountId;ExpectedVersion=$ExpectedVersion;ReasonCode=$ReasonCode;IssuedAt=$issued;ExpiresAt=$issued+300}
    $payload=[Text.Encoding]::UTF8.GetBytes(($command|ConvertTo-Json -Compress))
    $signature=$rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope=[ordered]@{OperatorId=$OperatorId;Payload=[Convert]::ToBase64String($payload);Signature=[Convert]::ToBase64String($signature)}
    $outputStream=[IO.File]::Open($OutputFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes(($envelope|ConvertTo-Json -Compress))
        $outputStream.Write($bytes,0,$bytes.Length)
    } finally {$outputStream.Dispose()}
    Write-Output "Signed account command created. OperationId=$OperationId; expires in 5 minutes."
} finally {$rsa.Dispose()}

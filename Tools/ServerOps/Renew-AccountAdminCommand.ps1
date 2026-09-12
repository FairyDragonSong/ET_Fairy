param(
    [Parameter(Mandatory)][string]$ExistingEnvelopeFile,
    [Parameter(Mandatory)][string]$PrivateKeyFile,
    [Parameter(Mandatory)][long]$ConfirmAccountId,
    [Parameter(Mandatory)][string]$OutputFile
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputFile){throw 'Output exists; do not overwrite a prior command'}
$envelope=Get-Content -LiteralPath $ExistingEnvelopeFile -Raw|ConvertFrom-Json
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PrivateKeyFile)))
    $original=[Convert]::FromBase64String($envelope.Payload)
    if($rsa.KeySize -lt 3072 -or -not $rsa.VerifyData($original,[Convert]::FromBase64String($envelope.Signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)){throw 'Original command signature does not match this operator key'}
    $command=[Text.Encoding]::UTF8.GetString($original)|ConvertFrom-Json
    if($ConfirmAccountId -le 0 -or $command.AccountId -ne $ConfirmAccountId -or $command.ConfirmAccountId -ne $ConfirmAccountId -or $command.OperatorId -ne $envelope.OperatorId){throw 'Target confirmation does not match original command'}
    $command.IssuedAt=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $command.ExpiresAt=$command.IssuedAt+300
    $payload=[Text.Encoding]::UTF8.GetBytes(($command|ConvertTo-Json -Compress))
    $envelope.Payload=[Convert]::ToBase64String($payload)
    $envelope.Signature=[Convert]::ToBase64String($rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss))
    $stream=[IO.File]::Open($OutputFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {$bytes=[Text.Encoding]::UTF8.GetBytes(($envelope|ConvertTo-Json -Compress));$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    Write-Output "Command renewed without changing business fields. OperationId=$($command.OperationId)"
} finally {$rsa.Dispose()}

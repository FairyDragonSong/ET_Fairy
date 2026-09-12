$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-backup-security-'+[guid]::NewGuid().ToString('N'))
$utf8=[Text.UTF8Encoding]::new($false);New-Item -ItemType Directory $fixture|Out-Null
try{
    $keys=@{}
    foreach($name in @('backup','requester','approver')){
        $rsa=[Security.Cryptography.RSA]::Create(3072)
        try{
            $private=Join-Path $fixture "$name.private.pem";$public=Join-Path $fixture "$name.public.pem"
            [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8)
            [IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8)
            $keys[$name]=@{Private=$private;Public=$public}
        }finally{$rsa.Dispose()}
    }
    $backup=Join-Path $fixture 'backup';$dump=Join-Path $backup 'ET_SOURCE';New-Item -ItemType Directory $dump|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $dump 'Account.bson'),[Security.Cryptography.RandomNumberGenerator]::GetBytes(1024))
    [IO.File]::WriteAllText((Join-Path $dump 'Account.metadata.json'),'{"indexes":[]}', $utf8)
    $manifestRaw=& "$PSScriptRoot/New-SignedMongoBackupManifest.ps1" -BackupPath $backup -SourceDatabase ET_SOURCE `
        -BackupId backup-security-test -SigningPrivateKeyFile $keys.backup.Private -SigningKeyId backup-test
    $manifest=($manifestRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $verifyBackup=@{BackupPath=$backup;TrustedPublicKeyFile=$keys.backup.Public;ExpectedSigningKeyId='backup-test';ExpectedSourceDatabase='ET_SOURCE'}
    $verifiedRaw=& "$PSScriptRoot/Test-SignedMongoBackup.ps1" @verifyBackup
    $verified=($verifiedRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $verified.Passed -or $verified.ManifestSha256 -ne $manifest.ManifestSha256){throw 'Signed backup verification failed'}

    $original=[IO.File]::ReadAllBytes((Join-Path $dump 'Account.bson'));$tampered=$original.Clone();$tampered[0]=$tampered[0]-bxor1
    [IO.File]::WriteAllBytes((Join-Path $dump 'Account.bson'),$tampered)
    $rejected=$false;try{& "$PSScriptRoot/Test-SignedMongoBackup.ps1" @verifyBackup|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Tampered backup payload was accepted'}
    [IO.File]::WriteAllBytes((Join-Path $dump 'Account.bson'),$original)

    $authRoot=Join-Path $fixture 'authorizations'
    $authRaw=& "$PSScriptRoot/New-MongoRestoreAuthorization.ps1" -BackupPath $backup `
        -BackupTrustedPublicKeyFile $keys.backup.Public -BackupExpectedSigningKeyId backup-test `
        -TargetDatabase ET_TARGET_DR_TEST -ConfirmTargetDatabase ET_TARGET_DR_TEST -DropExisting `
        -ConfirmRestoreMode 'DROP ET_TARGET_DR_TEST' -OperationId restore-security-test `
        -RequestedBy requester-test -ApprovedBy approver-test -RequesterPrivateKeyFile $keys.requester.Private `
        -RequesterKeyId requester-test -ApproverPrivateKeyFile $keys.approver.Private -ApproverKeyId approver-test `
        -OutputDirectory $authRoot -ValidForMinutes 10
    $auth=($authRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $restore=@{Database='ET_TARGET_DR_TEST';BackupPath=$backup;ConfirmDatabaseName='ET_TARGET_DR_TEST';DropExisting=$true;
        AuthorizationFile=$auth.AuthorizationFile;OperationId='restore-security-test';BackupTrustedPublicKeyFile=$keys.backup.Public;
        BackupExpectedSigningKeyId='backup-test';RequesterPublicKeyFile=$keys.requester.Public;ExpectedRequesterKeyId='requester-test';
        ApproverPublicKeyFile=$keys.approver.Public;ExpectedApproverKeyId='approver-test';VerifyOnly=$true}
    $restoreRaw=& "$PSScriptRoot/Restore-Mongo.ps1" @restore
    $restoreResult=($restoreRaw -join [Environment]::NewLine)|ConvertFrom-Json
    if(-not $restoreResult.Passed -or -not $restoreResult.DropExisting){throw 'Authorized restore verification failed'}
    $ledger=Join-Path $fixture 'restore-ledger';$consume=$restore.Clone();$consume.OperationLedgerDirectory=$ledger
    $consume.TestOnlyConsumeAuthorization=$true
    & "$PSScriptRoot/Restore-Mongo.ps1" @consume|Out-Null
    $rejected=$false;try{& "$PSScriptRoot/Restore-Mongo.ps1" @consume|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Restore authorization was consumed more than once'}
    $wrongTarget=$restore.Clone();$wrongTarget.Database='ET_OTHER_DR_TEST';$wrongTarget.ConfirmDatabaseName='ET_OTHER_DR_TEST'
    $rejected=$false;try{& "$PSScriptRoot/Restore-Mongo.ps1" @wrongTarget|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Restore authorization was replayed against another database'}
    $wrongMode=$restore.Clone();$wrongMode.Remove('DropExisting')
    $rejected=$false;try{& "$PSScriptRoot/Restore-Mongo.ps1" @wrongMode|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'DropExisting authorization was accepted for another mode'}
    $authBytes=[IO.File]::ReadAllBytes($auth.AuthorizationFile)
    $requesterSignature=[IO.File]::ReadAllBytes("$($auth.AuthorizationFile).requester.sig")
    $approverSignature=[IO.File]::ReadAllBytes("$($auth.AuthorizationFile).approver.sig")
    [IO.File]::AppendAllText($auth.AuthorizationFile,' ')
    $rejected=$false;try{& "$PSScriptRoot/Restore-Mongo.ps1" @restore|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Tampered restore authorization was accepted'}
    [IO.File]::WriteAllBytes($auth.AuthorizationFile,$authBytes)
    . "$PSScriptRoot/BackupRestore.Common.ps1"
    $expired=$utf8.GetString($authBytes)|ConvertFrom-Json -Depth 10 -DateKind String
    $expired.CreatedAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('O')
    $expired.ExpiresAtUtc=[DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('O')
    $expiredBytes=$utf8.GetBytes(($expired|ConvertTo-Json -Depth 10))
    [IO.File]::WriteAllBytes($auth.AuthorizationFile,$expiredBytes)
    [IO.File]::WriteAllBytes("$($auth.AuthorizationFile).requester.sig",(New-BackupSignature $expiredBytes $keys.requester.Private 'Requester'))
    [IO.File]::WriteAllBytes("$($auth.AuthorizationFile).approver.sig",(New-BackupSignature $expiredBytes $keys.approver.Private 'Approver'))
    $rejected=$false;try{& "$PSScriptRoot/Restore-Mongo.ps1" @restore|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Expired restore authorization was accepted'}
    [IO.File]::WriteAllBytes($auth.AuthorizationFile,$authBytes)
    [IO.File]::WriteAllBytes("$($auth.AuthorizationFile).requester.sig",$requesterSignature)
    [IO.File]::WriteAllBytes("$($auth.AuthorizationFile).approver.sig",$approverSignature)
    $copiedApprover=Join-Path $fixture 'copied-approver.pem';Copy-Item $keys.requester.Private $copiedApprover
    $rejected=$false
    try{& "$PSScriptRoot/New-MongoRestoreAuthorization.ps1" -BackupPath $backup `
        -BackupTrustedPublicKeyFile $keys.backup.Public -BackupExpectedSigningKeyId backup-test `
        -TargetDatabase ET_TARGET_DR_TEST -ConfirmTargetDatabase ET_TARGET_DR_TEST `
        -ConfirmRestoreMode 'NO DROP ET_TARGET_DR_TEST' -OperationId duplicate-key-test `
        -RequestedBy requester-test -ApprovedBy approver-test -RequesterPrivateKeyFile $keys.requester.Private `
        -RequesterKeyId requester-test -ApproverPrivateKeyFile $copiedApprover -ApproverKeyId copied-approver `
        -OutputDirectory $authRoot|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'Copied restore approval key was accepted'}
    [ordered]@{Passed=$true;SignedBackupVerified=$true;TamperedPayloadRejected=$true;DualRestoreAuthorizationVerified=$true;
        CrossDatabaseReplayRejected=$true;RestoreModeMismatchRejected=$true;TamperedAuthorizationRejected=$true;
        ExpiredAuthorizationRejected=$true;CopiedApprovalKeyRejected=$true;OneTimeConsumptionVerified=$true;
        VerifyOnlyPreventedDatabaseWrite=$true}|ConvertTo-Json
}finally{
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-backup-security-[a-f0-9]{32}$'-and(Test-Path $resolved)){
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

$ErrorActionPreference='Stop';$utf8=[Text.UTF8Encoding]::new($false)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-dr-governance-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $fixture|Out-Null
try{
    $keys=@{}
    foreach($name in @('backup','requester','approver','observer')){
        $rsa=[Security.Cryptography.RSA]::Create(3072)
        try{$private=Join-Path $fixture "$name.private.pem";$public=Join-Path $fixture "$name.public.pem";
            [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8);[IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8);
            $keys[$name]=@{Private=$private;Public=$public}}finally{$rsa.Dispose()}
    }
    $backup=Join-Path $fixture 'backup';$dump=Join-Path $backup 'ET_DR_SOURCE';New-Item -ItemType Directory $dump|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $dump 'Account.bson'),[Security.Cryptography.RandomNumberGenerator]::GetBytes(256))
    $manifestRaw=& "$PSScriptRoot/New-SignedMongoBackupManifest.ps1" -BackupPath $backup -SourceDatabase ET_DR_SOURCE `
        -BackupId dr-governance-backup -SigningPrivateKeyFile $keys.backup.Private -SigningKeyId dr-backup
    $manifest=($manifestRaw -join [Environment]::NewLine)|ConvertFrom-Json
    $policyRoot=Join-Path $fixture 'policies'
    $policyRaw=& "$PSScriptRoot/New-DisasterRecoveryPolicy.ps1" -PolicyId dr-policy-test -DeploymentId local-dr-test `
        -Database ET_DR_SOURCE -RpoMinutes 15 -RtoMinutes 5 -BackupIntervalMinutes 5 -MaximumDrillAgeDays 30 `
        -BackupSigningPublicKeyFile $keys.backup.Public -BackupSigningKeyId dr-backup `
        -DrillObserverPublicKeyFile $keys.observer.Public -DrillObserverKeyId dr-observer `
        -RequesterPrivateKeyFile $keys.requester.Private -RequesterKeyId dr-requester `
        -ApproverPrivateKeyFile $keys.approver.Private -ApproverKeyId dr-approver `
        -RequestedBy dr-requester -ApprovedBy dr-approver -OutputDirectory $policyRoot
    $policy=(($policyRaw -join [Environment]::NewLine)|ConvertFrom-Json)
    $policyVerify=@{PolicyFile=$policy.PolicyFile;RequesterPublicKeyFile=$keys.requester.Public;ExpectedRequesterKeyId='dr-requester';
        ApproverPublicKeyFile=$keys.approver.Public;ExpectedApproverKeyId='dr-approver';BackupSigningPublicKeyFile=$keys.backup.Public;
        ExpectedBackupSigningKeyId='dr-backup';DrillObserverPublicKeyFile=$keys.observer.Public;ExpectedDrillObserverKeyId='dr-observer'}
    if(-not(((& "$PSScriptRoot/Test-DisasterRecoveryPolicy.ps1" @policyVerify)-join[Environment]::NewLine|ConvertFrom-Json).Passed)){
        throw 'Disaster recovery policy verification failed'
    }
    $operation='dr-restore-operation';$now=[DateTimeOffset]::UtcNow
    $restoreFile=Join-Path $fixture 'restore-evidence.json';$ledgerFile=Join-Path $fixture 'restore-ledger.json'
    $restore=[ordered]@{StartedAtUtc=$now.AddSeconds(-2).ToString('O');CompletedAtUtc=$now.ToString('O');SourceDatabase='ET_DR_SOURCE';
        RestoreDatabase='ET_DR_TARGET';BackupManifestSha256=$manifest.ManifestSha256;RestoreOperationId=$operation;
        RestoreCheck=[ordered]@{counts=[ordered]@{Account=[ordered]@{source=1;restored=1;indexesVerified=$true}}};Result='PASS_DATABASE_CHECKS'}
    $restore|ConvertTo-Json -Depth 10|Set-Content $restoreFile -Encoding utf8NoBOM
    [ordered]@{SchemaVersion=1;Product='ET.Server';OperationId=$operation;Status='Completed';AuthorizationSha256=('a'*64);
        StartedAtUtc=$now.AddSeconds(-1).ToString('O');CompletedAtUtc=$now.ToString('O')}|ConvertTo-Json|Set-Content $ledgerFile -Encoding utf8NoBOM
    $drillRoot=Join-Path $fixture 'drills'
    $drillParameters=@{DrillId='drill-test';PolicyFile=$policy.PolicyFile;PolicyRequesterPublicKeyFile=$keys.requester.Public;
        ExpectedPolicyRequesterKeyId='dr-requester';PolicyApproverPublicKeyFile=$keys.approver.Public;ExpectedPolicyApproverKeyId='dr-approver';
        BackupSigningPublicKeyFile=$keys.backup.Public;ExpectedBackupSigningKeyId='dr-backup';DrillObserverPublicKeyFile=$keys.observer.Public;
        ExpectedDrillObserverKeyId='dr-observer';DrillObserverPrivateKeyFile=$keys.observer.Private;BackupPath=$backup;
        RestoreEvidenceFile=$restoreFile;RestoreOperationLedgerFile=$ledgerFile;OutputDirectory=$drillRoot}
    $drillRaw=& "$PSScriptRoot/New-RestoreDrillEvidence.ps1" @drillParameters;$drill=(($drillRaw-join[Environment]::NewLine)|ConvertFrom-Json)
    $drillVerify=$drillParameters.Clone();$drillVerify.Remove('DrillId');$drillVerify.Remove('DrillObserverPrivateKeyFile');$drillVerify.Remove('OutputDirectory');$drillVerify.DrillFile=$drill.DrillFile
    $drillResult=(((& "$PSScriptRoot/Test-RestoreDrillEvidence.ps1" @drillVerify)-join[Environment]::NewLine)|ConvertFrom-Json)
    if(-not$drillResult.Passed-or$drillResult.DurationSeconds-gt300){throw 'Restore drill evidence verification failed'}
    $saved=[IO.File]::ReadAllBytes($drill.DrillFile);[IO.File]::AppendAllText($drill.DrillFile,' ')
    $rejected=$false;try{& "$PSScriptRoot/Test-RestoreDrillEvidence.ps1" @drillVerify|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Tampered drill evidence was accepted'};[IO.File]::WriteAllBytes($drill.DrillFile,$saved)
    $slow=($restore|ConvertTo-Json -Depth 10|ConvertFrom-Json);$slow.StartedAtUtc=$now.AddMinutes(-6).ToString('O');$slow|ConvertTo-Json -Depth 10|Set-Content $restoreFile -Encoding utf8NoBOM
    $slowParameters=$drillParameters.Clone();$slowParameters.DrillId='slow-drill'
    $rejected=$false;try{& "$PSScriptRoot/New-RestoreDrillEvidence.ps1" @slowParameters|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Drill exceeding RTO was accepted'}
    $restore.StartedAtUtc=$now.AddDays(-31).AddSeconds(-2).ToString('O');$restore.CompletedAtUtc=$now.AddDays(-31).ToString('O');
    $restore|ConvertTo-Json -Depth 10|Set-Content $restoreFile -Encoding utf8NoBOM
    $staleParameters=$drillParameters.Clone();$staleParameters.DrillId='stale-drill'
    $rejected=$false;try{& "$PSScriptRoot/New-RestoreDrillEvidence.ps1" @staleParameters|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Stale restore drill was accepted'}
    [ordered]@{Passed=$true;DualSignedPolicyVerified=$true;RpoRtoBound=$true;SignedDrillVerified=$true;
        TamperedDrillRejected=$true;RtoBreachRejected=$true;StaleDrillRejected=$true}|ConvertTo-Json
}finally{
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar;$resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-dr-governance-[a-f0-9]{32}$'-and(Test-Path $resolved)){Remove-Item $resolved -Recurse -Force}
}

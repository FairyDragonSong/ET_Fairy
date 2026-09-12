$ErrorActionPreference='Stop';$utf8=[Text.UTF8Encoding]::new($false)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-storage-governance-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $fixture|Out-Null
try{
    $keys=@{};foreach($name in @('backup','policy-requester','policy-approver','drill-observer','storage-observer','inventory-requester','inventory-approver')){
        $rsa=[Security.Cryptography.RSA]::Create(3072);try{$private=Join-Path $fixture "$name.private.pem";$public=Join-Path $fixture "$name.public.pem";
            [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8);[IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8);$keys[$name]=@{Private=$private;Public=$public}}finally{$rsa.Dispose()}}
    $backup=Join-Path $fixture 'backup';$dump=Join-Path $backup 'ET_STORAGE';New-Item -ItemType Directory $dump|Out-Null
    [IO.File]::WriteAllBytes((Join-Path $dump 'Account.bson'),[Security.Cryptography.RandomNumberGenerator]::GetBytes(64))
    & "$PSScriptRoot/New-SignedMongoBackupManifest.ps1" -BackupPath $backup -SourceDatabase ET_STORAGE -BackupId storage-backup `
        -SigningPrivateKeyFile $keys.backup.Private -SigningKeyId storage-backup-key|Out-Null
    $policyRaw=& "$PSScriptRoot/New-DisasterRecoveryPolicy.ps1" -PolicyId storage-dr-policy -DeploymentId storage-test -Database ET_STORAGE `
        -RpoMinutes 15 -RtoMinutes 15 -BackupIntervalMinutes 5 -MaximumDrillAgeDays 30 -BackupSigningPublicKeyFile $keys.backup.Public `
        -BackupSigningKeyId storage-backup-key -DrillObserverPublicKeyFile $keys['drill-observer'].Public -DrillObserverKeyId drill-observer `
        -RequesterPrivateKeyFile $keys['policy-requester'].Private -RequesterKeyId policy-requester -ApproverPrivateKeyFile $keys['policy-approver'].Private `
        -ApproverKeyId policy-approver -RequestedBy policy-requester -ApprovedBy policy-approver -OutputDirectory (Join-Path $fixture 'policies')
    $policy=($policyRaw-join[Environment]::NewLine)|ConvertFrom-Json;$now=[DateTimeOffset]::UtcNow
    $copiesFile=Join-Path $fixture 'copies.json';$retentionFile=Join-Path $fixture 'retention.json'
    @([ordered]@{CopyId='copy-region-a';StorageLocation='vault-region-a';FailureDomain='region-a';ManifestSha256=(Get-FileHash (Join-Path $backup 'backup-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant();EncryptedAtRest=$true;EncryptionKeyId='kms-region-a';ObjectLockMode='Compliance';ImmutableUntilUtc=$now.AddDays(31).ToString('O');VerifiedAtUtc=$now.ToString('O')},
      [ordered]@{CopyId='copy-region-b';StorageLocation='vault-region-b';FailureDomain='region-b';ManifestSha256=(Get-FileHash (Join-Path $backup 'backup-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant();EncryptedAtRest=$true;EncryptionKeyId='kms-region-b';ObjectLockMode='Compliance';ImmutableUntilUtc=$now.AddDays(31).ToString('O');VerifiedAtUtc=$now.ToString('O')})|
        ConvertTo-Json -Depth 6|Set-Content $copiesFile -Encoding utf8NoBOM
    [ordered]@{DailyRestorePoints=7;WeeklyRestorePoints=4;MonthlyRestorePoints=3;OldestDailyUtc=$now.AddDays(-6).ToString('O');
        OldestWeeklyUtc=$now.AddDays(-21).ToString('O');OldestMonthlyUtc=$now.AddDays(-56).ToString('O')}|ConvertTo-Json|Set-Content $retentionFile -Encoding utf8NoBOM
    $parameters=@{InventoryId='storage-inventory-test';PolicyFile=$policy.PolicyFile;PolicyRequesterPublicKeyFile=$keys['policy-requester'].Public;
        ExpectedPolicyRequesterKeyId='policy-requester';PolicyApproverPublicKeyFile=$keys['policy-approver'].Public;ExpectedPolicyApproverKeyId='policy-approver';
        BackupSigningPublicKeyFile=$keys.backup.Public;ExpectedBackupSigningKeyId='storage-backup-key';DrillObserverPublicKeyFile=$keys['drill-observer'].Public;
        ExpectedDrillObserverKeyId='drill-observer';BackupPath=$backup;CopiesFile=$copiesFile;RetentionFile=$retentionFile;MinimumCopies=2;
        MinimumFailureDomains=2;MinimumImmutabilityDays=30;MinimumDailyRestorePoints=7;MinimumWeeklyRestorePoints=4;MinimumMonthlyRestorePoints=3;
        StorageObserverPrivateKeyFile=$keys['storage-observer'].Private;StorageObserverPublicKeyFile=$keys['storage-observer'].Public;StorageObserverKeyId='storage-observer';
        RequesterPrivateKeyFile=$keys['inventory-requester'].Private;RequesterKeyId='inventory-requester';ApproverPrivateKeyFile=$keys['inventory-approver'].Private;
        ApproverKeyId='inventory-approver';RequestedBy='inventory-requester';ApprovedBy='inventory-approver';OutputDirectory=(Join-Path $fixture 'inventories')}
    $raw=& "$PSScriptRoot/New-BackupStorageInventory.ps1" @parameters;$created=($raw-join[Environment]::NewLine)|ConvertFrom-Json
    $verify=$parameters.Clone();foreach($remove in @('CopiesFile','RetentionFile','MinimumCopies','MinimumFailureDomains','MinimumImmutabilityDays','MinimumDailyRestorePoints','MinimumWeeklyRestorePoints','MinimumMonthlyRestorePoints','StorageObserverPrivateKeyFile','StorageObserverKeyId','RequesterPrivateKeyFile','RequesterKeyId','ApproverPrivateKeyFile','ApproverKeyId','RequestedBy','ApprovedBy','OutputDirectory','InventoryId')){$verify.Remove($remove)}
    $verify.InventoryFile=$created.InventoryFile;$verify.RequesterPublicKeyFile=$keys['inventory-requester'].Public;$verify.ExpectedRequesterKeyId='inventory-requester';
    $verify.ApproverPublicKeyFile=$keys['inventory-approver'].Public;$verify.ExpectedApproverKeyId='inventory-approver';$verify.ExpectedStorageObserverKeyId='storage-observer'
    $result=(((& "$PSScriptRoot/Test-BackupStorageInventory.ps1" @verify)-join[Environment]::NewLine)|ConvertFrom-Json)
    if(-not$result.Passed-or$result.FailureDomains-ne2){throw 'Valid cross-domain storage inventory failed'}
    $badCopies=Get-Content $copiesFile -Raw|ConvertFrom-Json;$badCopies[1].FailureDomain='region-a';$badCopies|ConvertTo-Json -Depth 6|Set-Content $copiesFile -Encoding utf8NoBOM
    $bad=$parameters.Clone();$bad.InventoryId='same-domain-test';$rejected=$false;try{& "$PSScriptRoot/New-BackupStorageInventory.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Same-failure-domain copies were accepted'}
    $badCopies[1].FailureDomain='region-b';$badCopies[1].EncryptedAtRest=$false;$badCopies|ConvertTo-Json -Depth 6|Set-Content $copiesFile -Encoding utf8NoBOM
    $bad.InventoryId='unencrypted-test';$rejected=$false;try{& "$PSScriptRoot/New-BackupStorageInventory.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Unencrypted backup copy was accepted'}
    $badCopies[1].EncryptedAtRest=$true;$badCopies|ConvertTo-Json -Depth 6|Set-Content $copiesFile -Encoding utf8NoBOM
    $retention=Get-Content $retentionFile -Raw|ConvertFrom-Json;$retention.DailyRestorePoints=6;$retention|ConvertTo-Json|Set-Content $retentionFile -Encoding utf8NoBOM
    $bad.InventoryId='retention-gap-test';$rejected=$false;try{& "$PSScriptRoot/New-BackupStorageInventory.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Insufficient retention coverage was accepted'}
    $retention.DailyRestorePoints=7;$retention|ConvertTo-Json|Set-Content $retentionFile -Encoding utf8NoBOM
    . "$PSScriptRoot/BackupRestore.Common.ps1";$manifestFile=Join-Path $backup 'backup-manifest.json'
    $manifestBytes=[IO.File]::ReadAllBytes($manifestFile);$manifestSignature=[IO.File]::ReadAllBytes("$manifestFile.sig")
    $oldManifest=$utf8.GetString($manifestBytes)|ConvertFrom-Json -Depth 10 -DateKind String;$oldManifest.CreatedAtUtc=$now.AddHours(-1).ToString('O')
    $oldBytes=$utf8.GetBytes(($oldManifest|ConvertTo-Json -Depth 10));[IO.File]::WriteAllBytes($manifestFile,$oldBytes)
    [IO.File]::WriteAllBytes("$manifestFile.sig",(New-BackupSignature $oldBytes $keys.backup.Private 'Backup'))
    $bad.InventoryId='stale-rpo-test';$rejected=$false;try{& "$PSScriptRoot/New-BackupStorageInventory.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Backup older than RPO was accepted'}
    [IO.File]::WriteAllBytes($manifestFile,$manifestBytes);[IO.File]::WriteAllBytes("$manifestFile.sig",$manifestSignature)
    $saved=[IO.File]::ReadAllBytes($created.InventoryFile);[IO.File]::AppendAllText($created.InventoryFile,' ');$rejected=$false
    try{& "$PSScriptRoot/Test-BackupStorageInventory.ps1" @verify|Out-Null}catch{$rejected=$true};if(-not$rejected){throw 'Tampered storage inventory was accepted'}
    [IO.File]::WriteAllBytes($created.InventoryFile,$saved)
    [ordered]@{Passed=$true;TripleSignedInventoryVerified=$true;RpoFreshnessVerified=$true;TwoFailureDomainsVerified=$true;
        EncryptionAndObjectLockVerified=$true;RetentionCoverageVerified=$true;SameDomainRejected=$true;UnencryptedCopyRejected=$true;
        RetentionGapRejected=$true;StaleRpoBackupRejected=$true;TamperedInventoryRejected=$true}|ConvertTo-Json
}finally{$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar;$resolved=[IO.Path]::GetFullPath($fixture);
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-storage-governance-[a-f0-9]{32}$'-and(Test-Path $resolved)){Remove-Item $resolved -Recurse -Force}}

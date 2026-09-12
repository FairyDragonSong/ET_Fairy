function Assert-BackupStorageInventory($Inventory,$Policy,$Backup,[DateTimeOffset]$Now){
    $generated=[DateTimeOffset]::MinValue
    if($Inventory.SchemaVersion-ne1-or$Inventory.Product-ne'ET.Server'-or$Inventory.ArtifactType-ne'BackupStorageInventory'-or
       $Inventory.InventoryId-notmatch'^[0-9A-Za-z._-]{3,80}$'-or$Inventory.StorageObserverKeyId-notmatch'^[0-9A-Za-z._-]{3,64}$'-or
       $Inventory.RequesterKeyId-notmatch'^[0-9A-Za-z._-]{3,64}$'-or$Inventory.ApproverKeyId-notmatch'^[0-9A-Za-z._-]{3,64}$'-or
       $Inventory.RequestedBy-notmatch'^[0-9A-Za-z._@-]{3,80}$'-or$Inventory.ApprovedBy-notmatch'^[0-9A-Za-z._@-]{3,80}$'-or
       $Inventory.RequestedBy-ceq$Inventory.ApprovedBy){throw 'Backup storage inventory schema is invalid'}
    if($Inventory.PolicyId-cne$Policy.PolicyId-or$Inventory.PolicySha256-cne$Policy.PolicySha256-or
       $Inventory.DeploymentId-cne$Policy.DeploymentId-or$Inventory.Database-cne$Policy.Database-or
       $Inventory.Backup.BackupId-cne$Backup.BackupId-or$Inventory.Backup.ManifestSha256-cne$Backup.ManifestSha256-or
       ([DateTimeOffset]$Inventory.Backup.CreatedAtUtc)-ne([DateTimeOffset]$Backup.CreatedAtUtc)){
        throw 'Backup storage inventory policy or backup binding is invalid'
    }
    if(-not[DateTimeOffset]::TryParse([string]$Inventory.GeneratedAtUtc,[ref]$generated)-or$generated-gt$Now.AddMinutes(5)-or
       $generated-lt$Now.AddMinutes(-15)){throw 'Backup storage inventory observation time is invalid'}
    $backupCreated=[DateTimeOffset]$Backup.CreatedAtUtc
    if($backupCreated-gt$generated-or($generated-$backupCreated).TotalMinutes-gt[int]$Policy.RpoMinutes){throw 'Latest signed backup exceeds RPO'}
    $requirements=$Inventory.Requirements
    if([int]$requirements.MinimumCopies-lt2-or[int]$requirements.MinimumCopies-gt5-or
       [int]$requirements.MinimumFailureDomains-lt2-or[int]$requirements.MinimumFailureDomains-gt[int]$requirements.MinimumCopies-or
       [int]$requirements.MinimumImmutabilityDays-lt7-or[int]$requirements.MinimumImmutabilityDays-gt3650-or
       [int]$requirements.MinimumDailyRestorePoints-lt7-or[int]$requirements.MinimumWeeklyRestorePoints-lt4-or
       [int]$requirements.MinimumMonthlyRestorePoints-lt3){throw 'Backup storage requirements are below the commercial floor'}
    $copies=@($Inventory.Copies)
    if($copies.Count-lt[int]$requirements.MinimumCopies-or$copies.Count-gt5){throw 'Backup copy count is below policy'}
    $copyIds=@{};$locations=@{};$domains=@{}
    foreach($copy in $copies){
        $immutable=[DateTimeOffset]::MinValue;$verified=[DateTimeOffset]::MinValue
        if($copy.CopyId-notmatch'^[0-9A-Za-z._-]{3,80}$'-or$copy.StorageLocation-notmatch'^[0-9A-Za-z._-]{3,120}$'-or
           $copy.FailureDomain-notmatch'^[0-9A-Za-z._-]{3,80}$'-or$copy.ManifestSha256-cne$Backup.ManifestSha256-or
           -not[bool]$copy.EncryptedAtRest-or$copy.EncryptionKeyId-notmatch'^[0-9A-Za-z._-]{3,120}$'-or
           $copy.ObjectLockMode-ne'Compliance'-or-not[DateTimeOffset]::TryParse([string]$copy.ImmutableUntilUtc,[ref]$immutable)-or
           -not[DateTimeOffset]::TryParse([string]$copy.VerifiedAtUtc,[ref]$verified)-or$verified-lt$backupCreated-or$verified-gt$generated.AddMinutes(5)-or
           $immutable-lt$backupCreated.AddDays([int]$requirements.MinimumImmutabilityDays)){
            throw 'Backup copy does not satisfy encryption, object lock, identity, or verification requirements'
        }
        if($copyIds.ContainsKey([string]$copy.CopyId)-or$locations.ContainsKey([string]$copy.StorageLocation)){throw 'Backup copies are duplicated'}
        $copyIds[[string]$copy.CopyId]=$true;$locations[[string]$copy.StorageLocation]=$true;$domains[[string]$copy.FailureDomain]=$true
    }
    if($domains.Count-lt[int]$requirements.MinimumFailureDomains){throw 'Backup copies do not span enough failure domains'}
    $retention=$Inventory.Retention
    $oldestDaily=[DateTimeOffset]::MinValue;$oldestWeekly=[DateTimeOffset]::MinValue;$oldestMonthly=[DateTimeOffset]::MinValue
    if([int]$retention.DailyRestorePoints-lt[int]$requirements.MinimumDailyRestorePoints-or
       [int]$retention.WeeklyRestorePoints-lt[int]$requirements.MinimumWeeklyRestorePoints-or
       [int]$retention.MonthlyRestorePoints-lt[int]$requirements.MinimumMonthlyRestorePoints-or
       -not[DateTimeOffset]::TryParse([string]$retention.OldestDailyUtc,[ref]$oldestDaily)-or
       -not[DateTimeOffset]::TryParse([string]$retention.OldestWeeklyUtc,[ref]$oldestWeekly)-or
       -not[DateTimeOffset]::TryParse([string]$retention.OldestMonthlyUtc,[ref]$oldestMonthly)-or
       $oldestDaily-gt$generated.AddDays(-([int]$requirements.MinimumDailyRestorePoints-1))-or
       $oldestWeekly-gt$generated.AddDays(-7*([int]$requirements.MinimumWeeklyRestorePoints-1))-or
       $oldestMonthly-gt$generated.AddDays(-28*([int]$requirements.MinimumMonthlyRestorePoints-1))){
        throw 'Backup retention coverage is below policy'
    }
}

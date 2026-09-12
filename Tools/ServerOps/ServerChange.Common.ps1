$script:ServerChangeUtf8=[Text.UTF8Encoding]::new($false)
$script:RequiredServerChangeItems=@(
    'ci-hosted-run','dependency-scan','secret-scan','release-signature','sbom-license-review',
    'rollout-plan','rollback-verified','deployment-state','emergency-stop','backup-restore-drill',
    'backup-storage-inventory','tls-ingress','firewall-ddos','secret-manager','monitoring-alerts',
    'oncall-approved','open-high-risk-findings'
)

function Assert-ServerChangeRecord($Record,[DateTimeOffset]$Now,[bool]$AllowTestRecord,[bool]$RequireActiveWindow){
    if([int]$Record.SchemaVersion-ne1-or$Record.Product-cne'ET.Server'-or$Record.ArtifactType-cne'ServerChangeRecord'){
        throw 'Server change record schema is invalid'
    }
    if($Record.RecordId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'-or
       $Record.ChangeId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'-or
       $Record.DeploymentId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'){
        throw 'Server change identity is invalid'
    }
    if($Record.Environment -notin @('Staging','Production')-or$Record.RiskLevel -notin @('Low','Medium','High','Emergency')-or
       $Record.Decision-cne'APPROVED'){throw 'Server change classification or decision is invalid'}
    if([bool]$Record.TestOnly-and-not$AllowTestRecord){throw 'Test-only change record is not accepted for deployment'}
    if($Record.RequestedBy-ceq$Record.ApprovedBy-or$Record.RequesterKeyId-ceq$Record.ApproverKeyId-or
       $Record.RequesterPublicKeySha256-ceq$Record.ApproverPublicKeySha256){throw 'Change requester and approver must be distinct'}
    $created=[DateTimeOffset]::Parse($Record.CreatedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
    $start=[DateTimeOffset]::Parse($Record.WindowStartUtc,[Globalization.CultureInfo]::InvariantCulture)
    $end=[DateTimeOffset]::Parse($Record.WindowEndUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($start-ge$end-or($end-$start).TotalHours-gt24-or$created-gt$end){throw 'Change window is invalid'}
    if($RequireActiveWindow-and($Now-lt$start-or$Now-gt$end)){throw 'Change record is outside its approved window'}
    $items=@($Record.Checklist)
    if($items.Count-ne$script:RequiredServerChangeItems.Count){throw 'Change checklist item count is invalid'}
    $seen=@{}
    foreach($item in $items){
        if($item.ItemId -notin $script:RequiredServerChangeItems-or$seen.ContainsKey([string]$item.ItemId)){throw 'Change checklist contains an unknown or duplicate item'}
        $seen[[string]$item.ItemId]=$true
        if($item.Status-cne'PASS'){throw "Change checklist item $($item.ItemId) did not pass"}
        $observed=[DateTimeOffset]::Parse($item.ObservedAtUtc,[Globalization.CultureInfo]::InvariantCulture)
        $validUntil=[DateTimeOffset]::Parse($item.ValidUntilUtc,[Globalization.CultureInfo]::InvariantCulture)
        if($observed-gt$created.AddMinutes(5)-or$validUntil-lt$end-or$observed-gt$validUntil){throw "Change checklist item $($item.ItemId) is stale or has an invalid validity window"}
        $file=[IO.Path]::GetFullPath([string]$item.EvidenceFile)
        if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Change evidence is missing: $($item.ItemId)"}
        if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()-cne$item.EvidenceSha256){throw "Change evidence hash mismatch: $($item.ItemId)"}
        if(-not[bool]$Record.TestOnly){Assert-BackupPathOutsideRepository $file "Evidence $($item.ItemId)"|Out-Null}
    }
    foreach($required in $script:RequiredServerChangeItems){if(-not$seen.ContainsKey($required)){throw "Required change checklist item is missing: $required"}}
    $risk=@($items|Where-Object ItemId -CEQ 'open-high-risk-findings')[0]
    try{$riskEvidence=Get-Content -LiteralPath $risk.EvidenceFile -Raw|ConvertFrom-Json -Depth 5}catch{throw 'Open-risk evidence is not valid JSON'}
    if($null-eq$riskEvidence.OpenHighRiskFindings-or[int]$riskEvidence.OpenHighRiskFindings-ne0){throw 'Open high-risk findings block deployment'}
}

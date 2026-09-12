$script:SecurityAssessmentUtf8=[Text.UTF8Encoding]::new($false)
$script:RequiredSecurityAssessmentTypes=@('DependencyScan','SecretScan','PermissionAudit','PenetrationTest','FaultInjection')

function Assert-ServerSecurityAssessment($Report,[DateTimeOffset]$Now,[bool]$AllowTestReport){
    if([int]$Report.SchemaVersion-ne1-or$Report.Product-cne'ET.Server'-or$Report.ArtifactType-cne'ServerSecurityAssessment'){
        throw 'Security assessment schema is invalid'
    }
    if($Report.AssessmentId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'-or$Report.DeploymentId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'-or
       [string]::IsNullOrWhiteSpace([string]$Report.ReleaseVersion)-or[string]::IsNullOrWhiteSpace([string]$Report.AssessedBy)-or
       [string]::IsNullOrWhiteSpace([string]$Report.RequestedBy)-or[string]::IsNullOrWhiteSpace([string]$Report.ApprovedBy)){
        throw 'Security assessment identity is invalid'
    }
    if([bool]$Report.TestOnly-and-not$AllowTestReport){throw 'Test-only security assessment is not accepted for deployment'}
    if($Report.AssessedBy-ceq$Report.RequestedBy-or$Report.AssessedBy-ceq$Report.ApprovedBy-or$Report.RequestedBy-ceq$Report.ApprovedBy){
        throw 'Security assessor, requester and approver must be distinct'
    }
    $created=[DateTimeOffset]::Parse($Report.CreatedAtUtc,[Globalization.CultureInfo]::InvariantCulture);$validUntil=[DateTimeOffset]::Parse($Report.ValidUntilUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($validUntil-le$Now-or$created-gt$Now.AddMinutes(5)-or($validUntil-$created).TotalDays-gt30){throw 'Security assessment has expired or has an invalid validity window'}
    $types=@{};foreach($evidence in @($Report.AssessmentEvidence)){
        if($evidence.Type -notin $script:RequiredSecurityAssessmentTypes-or$types.ContainsKey([string]$evidence.Type)){throw 'Security assessment evidence type is unknown or duplicated'}
        $types[[string]$evidence.Type]=$true;$file=[IO.Path]::GetFullPath([string]$evidence.EvidenceFile)
        if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Security assessment evidence is missing: $($evidence.Type)"}
        if((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()-cne$evidence.EvidenceSha256){throw "Security assessment evidence hash mismatch: $($evidence.Type)"}
        if(-not[bool]$Report.TestOnly){Assert-BackupPathOutsideRepository $file "Security evidence $($evidence.Type)"|Out-Null}
    }
    foreach($type in $script:RequiredSecurityAssessmentTypes){if(-not$types.ContainsKey($type)){throw "Required security assessment is missing: $type"}}
    $findingIds=@{};$openHigh=0
    foreach($finding in @($Report.Findings)){
        if($finding.FindingId-notmatch'^[a-zA-Z0-9][a-zA-Z0-9._-]{2,127}$'-or$findingIds.ContainsKey([string]$finding.FindingId)){throw 'Security finding identity is invalid or duplicated'}
        $findingIds[[string]$finding.FindingId]=$true
        if($finding.Severity -notin @('Critical','High','Medium','Low')-or$finding.Status -notin @('Open','Closed','RiskAccepted')){throw 'Security finding classification is invalid'}
        if($finding.Severity -in @('Critical','High')-and$finding.Status-cne'Closed'){$openHigh++}
        if($finding.Status-ceq'Closed'-and([string]::IsNullOrWhiteSpace([string]$finding.ResolutionEvidence))){throw 'Closed security finding requires resolution evidence'}
        if($finding.Status-ceq'RiskAccepted'){
            if($finding.Severity -in @('Critical','High')){throw 'Critical or high security risk cannot be accepted for release'}
            if([string]::IsNullOrWhiteSpace([string]$finding.RiskAcceptanceReason)-or[DateTimeOffset]::Parse($finding.RiskAcceptanceExpiresAtUtc)-le$Now){throw 'Risk acceptance is missing a reason or has expired'}
        }
    }
    if($openHigh-ne0-or[int]$Report.OpenHighRiskFindings-ne0){throw 'Open critical or high security findings block deployment'}
}

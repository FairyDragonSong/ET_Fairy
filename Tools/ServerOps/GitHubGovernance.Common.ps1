$script:GitHubGovernanceUtf8=[Text.UTF8Encoding]::new($false)
function Assert-GitHubProductionGovernance($Evidence,[DateTimeOffset]$Now,[bool]$AllowTestEvidence){
    if([int]$Evidence.SchemaVersion-ne1-or$Evidence.Product-cne'ET.Server'-or$Evidence.ArtifactType-cne'GitHubProductionGovernance'){throw 'GitHub governance evidence schema is invalid'}
    if([bool]$Evidence.TestOnly-and-not$AllowTestEvidence){throw 'Test-only GitHub governance evidence is not accepted for deployment'}
    if($Evidence.CommitSha-notmatch'^[a-f0-9]{40}$'-or$Evidence.BranchHeadSha-cne$Evidence.CommitSha){throw 'GitHub governance evidence is not bound to branch HEAD'}
    $observed=[DateTimeOffset]::Parse($Evidence.ObservedAtUtc);$valid=[DateTimeOffset]::Parse($Evidence.ValidUntilUtc)
    if($observed-gt$Now.AddMinutes(5)-or$valid-le$Now-or($valid-$observed).TotalHours-gt1){throw 'GitHub governance evidence is stale or has an invalid validity window'}
    $p=$Evidence.BranchProtection
    if(-not[bool]$p.RequiredStatusChecksStrict-or$Evidence.RequiredCheckName -notin @($p.RequiredChecks)-or-not[bool]$p.EnforceAdmins-or
       [int]$p.RequiredApprovingReviewCount-lt2-or-not[bool]$p.DismissStaleReviews-or-not[bool]$p.RequireCodeOwnerReviews-or
       -not[bool]$p.RequireLastPushApproval-or-not[bool]$p.RequireConversationResolution-or[bool]$p.AllowForcePushes-or[bool]$p.AllowDeletions){
        throw 'GitHub branch protection does not meet production policy'
    }
    $check=$Evidence.HostedCheck
    if($check.Name-cne$Evidence.RequiredCheckName-or$check.HeadSha-cne$Evidence.CommitSha-or$check.Status-cne'completed'-or$check.Conclusion-cne'success'){
        throw 'Required hosted CI check did not succeed for the exact commit'
    }
}

param(
    [Parameter(Mandatory)][string]$AssessmentId,[Parameter(Mandatory)][string]$DeploymentId,[Parameter(Mandatory)][string]$ReleaseVersion,
    [Parameter(Mandatory)][string]$AssessmentEvidenceFile,[Parameter(Mandatory)][string]$FindingsFile,
    [Parameter(Mandatory)][DateTimeOffset]$ValidUntilUtc,[Parameter(Mandatory)][string]$AssessedBy,
    [Parameter(Mandatory)][string]$RequestedBy,[Parameter(Mandatory)][string]$ApprovedBy,
    [Parameter(Mandatory)][string]$AssessorPrivateKeyFile,[Parameter(Mandatory)][string]$AssessorKeyId,
    [Parameter(Mandatory)][string]$RequesterPrivateKeyFile,[Parameter(Mandatory)][string]$RequesterKeyId,
    [Parameter(Mandatory)][string]$ApproverPrivateKeyFile,[Parameter(Mandatory)][string]$ApproverKeyId,
    [Parameter(Mandatory)][string]$ReleaseSigningPublicKeyFile,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$TestOnlyReport
)
$ErrorActionPreference='Stop';. "$PSScriptRoot/BackupRestore.Common.ps1";. "$PSScriptRoot/SecurityAssessment.Common.ps1"
$assessor=Get-BackupKeyFingerprint $AssessorPrivateKeyFile 'Security assessor' $true;$requester=Get-BackupKeyFingerprint $RequesterPrivateKeyFile 'Security requester' $true
$approver=Get-BackupKeyFingerprint $ApproverPrivateKeyFile 'Security approver' $true;$release=Get-BackupKeyFingerprint $ReleaseSigningPublicKeyFile 'Release signer' $false
if(@(@($assessor,$requester,$approver,$release)|Select-Object -Unique).Count-ne4){throw 'Security assessment trust roles are not cryptographically separated'}
$sources=Get-Content ([IO.Path]::GetFullPath($AssessmentEvidenceFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String
$evidence=@();foreach($source in @($sources)){$file=[IO.Path]::GetFullPath([string]$source.EvidenceFile);if(-not(Test-Path $file -PathType Leaf)){throw "Security evidence is missing: $($source.Type)"}
    if(-not$TestOnlyReport){Assert-BackupPathOutsideRepository $file "Security evidence $($source.Type)"|Out-Null}
    $evidence+=[ordered]@{Type=[string]$source.Type;EvidenceFile=$file;EvidenceSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}}
$findings=@(Get-Content ([IO.Path]::GetFullPath($FindingsFile)) -Raw|ConvertFrom-Json -Depth 8 -DateKind String)
$report=[ordered]@{SchemaVersion=1;Product='ET.Server';ArtifactType='ServerSecurityAssessment';AssessmentId=$AssessmentId;DeploymentId=$DeploymentId;
    ReleaseVersion=$ReleaseVersion;CreatedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');ValidUntilUtc=$ValidUntilUtc.ToUniversalTime().ToString('O');TestOnly=[bool]$TestOnlyReport;
    AssessedBy=$AssessedBy;RequestedBy=$RequestedBy;ApprovedBy=$ApprovedBy;AssessorKeyId=$AssessorKeyId;RequesterKeyId=$RequesterKeyId;ApproverKeyId=$ApproverKeyId;
    AssessorPublicKeySha256=$assessor;RequesterPublicKeySha256=$requester;ApproverPublicKeySha256=$approver;ReleaseSigningPublicKeySha256=$release;
    OpenHighRiskFindings=0;AssessmentEvidence=$evidence;Findings=$findings}
Assert-ServerSecurityAssessment $report ([DateTimeOffset]::UtcNow) ([bool]$TestOnlyReport)
$output=Assert-BackupPathOutsideRepository $OutputDirectory 'OutputDirectory';New-Item -ItemType Directory $output -Force|Out-Null;$file=Join-Path $output "$AssessmentId.security-assessment.json"
if((Test-Path $file)-or(Test-Path "$file.assessor.sig")-or(Test-Path "$file.requester.sig")-or(Test-Path "$file.approver.sig")){throw 'Security assessment already exists'}
$bytes=$script:SecurityAssessmentUtf8.GetBytes(($report|ConvertTo-Json -Depth 12));try{[IO.File]::WriteAllBytes($file,$bytes)
    [IO.File]::WriteAllBytes("$file.assessor.sig",(New-BackupSignature $bytes $AssessorPrivateKeyFile 'Security assessor'));[IO.File]::WriteAllBytes("$file.requester.sig",(New-BackupSignature $bytes $RequesterPrivateKeyFile 'Security requester'))
    [IO.File]::WriteAllBytes("$file.approver.sig",(New-BackupSignature $bytes $ApproverPrivateKeyFile 'Security approver'));[ordered]@{Passed=$true;AssessmentFile=$file;AssessmentId=$AssessmentId;OpenHighRiskFindings=0;AssessmentSha256=(Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
}catch{foreach($path in @($file,"$file.assessor.sig","$file.requester.sig","$file.approver.sig")){if(Test-Path $path){Remove-Item $path -Force}};throw}

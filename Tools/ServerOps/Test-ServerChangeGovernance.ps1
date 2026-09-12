$ErrorActionPreference='Stop';$utf8=[Text.UTF8Encoding]::new($false)
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('et-change-governance-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $fixture|Out-Null
try{
    $keys=@{};foreach($name in @('requester','approver','release','state')){
        $rsa=[Security.Cryptography.RSA]::Create(3072);try{$private=Join-Path $fixture "$name.private.pem";$public=Join-Path $fixture "$name.public.pem";
            [IO.File]::WriteAllText($private,$rsa.ExportPkcs8PrivateKeyPem(),$utf8);[IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem(),$utf8);
            $keys[$name]=@{Private=$private;Public=$public}}finally{$rsa.Dispose()}
    }
    . "$PSScriptRoot/ServerChange.Common.ps1";. "$PSScriptRoot/BackupRestore.Common.ps1"
    $now=[DateTimeOffset]::UtcNow;$evidenceRoot=Join-Path $fixture 'evidence';New-Item -ItemType Directory $evidenceRoot|Out-Null
    $checklist=@();foreach($itemId in $script:RequiredServerChangeItems){
        $evidence=Join-Path $evidenceRoot "$itemId.json"
        $payload=if($itemId-ceq'open-high-risk-findings'){[ordered]@{OpenHighRiskFindings=0;ScanId='security-review-1'}}else{[ordered]@{Passed=$true;EvidenceId="$itemId-evidence"}}
        [IO.File]::WriteAllText($evidence,($payload|ConvertTo-Json),$utf8)
        $checklist+=[ordered]@{ItemId=$itemId;Status='PASS';EvidenceFile=$evidence;ObservedAtUtc=$now.AddMinutes(-2).ToString('O');ValidUntilUtc=$now.AddHours(3).ToString('O')}
    }
    $checklistFile=Join-Path $fixture 'checklist.json';[IO.File]::WriteAllText($checklistFile,($checklist|ConvertTo-Json -Depth 6),$utf8)
    $parameters=@{RecordId='change-record-test';ChangeId='change-test-1';DeploymentId='deployment-test';Environment='Staging';RiskLevel='Medium';
        WindowStartUtc=$now.AddMinutes(-1);WindowEndUtc=$now.AddHours(1);ChecklistFile=$checklistFile;RequestedBy='release-requester';ApprovedBy='release-approver';
        RequesterPrivateKeyFile=$keys.requester.Private;RequesterKeyId='change-requester';ApproverPrivateKeyFile=$keys.approver.Private;ApproverKeyId='change-approver';
        ReleaseSigningPublicKeyFile=$keys.release.Public;DeploymentStatePublicKeyFile=$keys.state.Public;OutputDirectory=(Join-Path $fixture 'records');TestOnlyRecord=$true}
    $created=(((& "$PSScriptRoot/New-ServerChangeRecord.ps1" @parameters)-join[Environment]::NewLine)|ConvertFrom-Json)
    $verify=@{RecordFile=$created.RecordFile;RequesterPublicKeyFile=$keys.requester.Public;ExpectedRequesterKeyId='change-requester';
        ApproverPublicKeyFile=$keys.approver.Public;ExpectedApproverKeyId='change-approver';ReleaseSigningPublicKeyFile=$keys.release.Public;
        DeploymentStatePublicKeyFile=$keys.state.Public;AllowTestRecord=$true;RequireActiveWindow=$true}
    $valid=(((& "$PSScriptRoot/Test-ServerChangeRecord.ps1" @verify)-join[Environment]::NewLine)|ConvertFrom-Json)
    if(-not$valid.Passed-or$valid.ChecklistItems-ne$script:RequiredServerChangeItems.Count){throw 'Valid server change record failed'}
    $productionVerify=$verify.Clone();$productionVerify.Remove('AllowTestRecord');$rejected=$false
    try{& "$PSScriptRoot/Test-ServerChangeRecord.ps1" @productionVerify|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Test-only change record was accepted for deployment'}

    $missing=@($checklist|Where-Object ItemId -CNE 'monitoring-alerts');[IO.File]::WriteAllText($checklistFile,($missing|ConvertTo-Json -Depth 6),$utf8)
    $bad=$parameters.Clone();$bad.RecordId='missing-item';$rejected=$false;try{& "$PSScriptRoot/New-ServerChangeRecord.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Missing checklist item was accepted'}
    [IO.File]::WriteAllText($checklistFile,($checklist|ConvertTo-Json -Depth 6),$utf8)

    $riskFile=Join-Path $evidenceRoot 'open-high-risk-findings.json';$riskOriginal=[IO.File]::ReadAllBytes($riskFile)
    [IO.File]::WriteAllText($riskFile,([ordered]@{OpenHighRiskFindings=1}|ConvertTo-Json),$utf8)
    $bad=$parameters.Clone();$bad.RecordId='open-risk';$rejected=$false;try{& "$PSScriptRoot/New-ServerChangeRecord.ps1" @bad|Out-Null}catch{$rejected=$true}
    if(-not$rejected){throw 'Open high-risk finding was accepted'};[IO.File]::WriteAllBytes($riskFile,$riskOriginal)

    $bad=$parameters.Clone();$bad.RecordId='same-approver';$bad.ApproverPrivateKeyFile=$keys.requester.Private;$rejected=$false
    try{& "$PSScriptRoot/New-ServerChangeRecord.ps1" @bad|Out-Null}catch{$rejected=$true};if(-not$rejected){throw 'Same approval key was accepted'}

    $tamperFile=Join-Path $evidenceRoot 'monitoring-alerts.json';[IO.File]::AppendAllText($tamperFile,' ');$rejected=$false
    try{& "$PSScriptRoot/Test-ServerChangeRecord.ps1" @verify|Out-Null}catch{$rejected=$true};if(-not$rejected){throw 'Tampered evidence was accepted'}
    [IO.File]::WriteAllText($tamperFile,([ordered]@{Passed=$true;EvidenceId='monitoring-alerts-evidence'}|ConvertTo-Json),$utf8)

    $recordBytes=[IO.File]::ReadAllBytes($created.RecordFile);$record=$utf8.GetString($recordBytes)|ConvertFrom-Json -Depth 12 -DateKind String
    $record.WindowStartUtc=$now.AddHours(-2).ToString('O');$record.WindowEndUtc=$now.AddHours(-1).ToString('O');$expired=$utf8.GetBytes(($record|ConvertTo-Json -Depth 12))
    [IO.File]::WriteAllBytes($created.RecordFile,$expired)
    [IO.File]::WriteAllBytes("$($created.RecordFile).requester.sig",(New-BackupSignature $expired $keys.requester.Private 'Change requester'))
    [IO.File]::WriteAllBytes("$($created.RecordFile).approver.sig",(New-BackupSignature $expired $keys.approver.Private 'Change approver'))
    $rejected=$false;try{& "$PSScriptRoot/Test-ServerChangeRecord.ps1" @verify|Out-Null}catch{$rejected=$true};if(-not$rejected){throw 'Expired change window was accepted'}

    [ordered]@{Passed=$true;RequiredItems=$script:RequiredServerChangeItems.Count;DualApprovalVerified=$true;RoleSeparationVerified=$true;
        TestOnlyProductionUseRejected=$true;MissingItemRejected=$true;OpenHighRiskRejected=$true;EvidenceTamperRejected=$true;ExpiredWindowRejected=$true}|ConvertTo-Json
}finally{
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar;$resolved=[IO.Path]::GetFullPath($fixture)
    if($resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-and[IO.Path]::GetFileName($resolved)-match'^et-change-governance-[a-f0-9]{32}$'-and(Test-Path $resolved)){Remove-Item $resolved -Recurse -Force}
}

$script:DeploymentStateUtf8=[Text.UTF8Encoding]::new($false)

function Get-DeploymentFullPath([string]$Path){
    return [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
}

function Assert-DeploymentPathOutsideRepository([string]$Path,[string]$Name){
    $full=Get-DeploymentFullPath $Path
    $repositoryRoot=Get-DeploymentFullPath (Join-Path $PSScriptRoot '../..')
    if($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/')){throw "$Name cannot be a drive root"}
    if(($full+[IO.Path]::DirectorySeparatorChar).StartsWith(
        $repositoryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
        throw "$Name must be outside the source repository"
    }
    return $full
}

function Get-RsaPublicFingerprint([string]$KeyFile){
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText((Get-DeploymentFullPath $KeyFile)))
        if($rsa.KeySize -lt 3072){throw 'Deployment state RSA key must be at least 3072 bits'}
        $bytes=$rsa.ExportSubjectPublicKeyInfo()
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    finally{$rsa.Dispose()}
}

function Test-DeploymentStateSignature([byte[]]$Bytes,[byte[]]$Signature,[string]$PublicKeyFile){
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText((Get-DeploymentFullPath $PublicKeyFile)))
        if($rsa.KeySize -lt 3072){throw 'Deployment state RSA key must be at least 3072 bits'}
        return $rsa.VerifyData($Bytes,$Signature,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally{$rsa.Dispose()}
}

function Assert-DeploymentStateShape($State,[string]$ExpectedKeyId,[string]$Fingerprint){
    $slotPattern='^[0-9A-Za-z.-]+-[a-f0-9]{12}$';$hashPattern='^[a-f0-9]{64}$'
    if($State.SchemaVersion -ne 1 -or $State.Product -ne 'ET.Server' -or
       $State.StateSignerKeyId -cne $ExpectedKeyId -or $State.StateSignerPublicKeySha256 -cne $Fingerprint -or
       [int64]$State.Generation -lt 1 -or $State.DeploymentId -notmatch '^[0-9A-Za-z._-]{3,80}$' -or
       $State.Phase -notin @('Stable','CandidateStaged','SwitchPending','RollbackPending','Stopped') -or
       $State.ActiveSlot -notmatch $slotPattern -or $State.DesiredSlot -notmatch $slotPattern){
        throw 'Deployment state identity or schema is invalid'
    }
    if($State.Phase -eq 'Stable' -and ($State.ActiveSlot -cne $State.DesiredSlot -or $State.CandidateSlot)){
        throw 'Stable deployment state invariant is invalid'
    }
    if($State.Phase -eq 'CandidateStaged' -and ($State.ActiveSlot -cne $State.DesiredSlot -or
       $State.PreviousSlot -or $State.CandidateSlot -notmatch $slotPattern -or
       $State.PlanSha256 -notmatch $hashPattern -or $State.DecisionObservationSha256)){
        throw 'Candidate-staged deployment state invariant is invalid'
    }
    if($State.Phase -eq 'SwitchPending' -and ($State.PreviousSlot -cne $State.ActiveSlot -or
       $State.CandidateSlot -notmatch $slotPattern -or $State.DesiredSlot -cne $State.CandidateSlot -or
       $State.PlanSha256 -notmatch $hashPattern -or $State.DecisionObservationSha256 -notmatch $hashPattern)){
        throw 'Switch-pending deployment state invariant is invalid'
    }
    if($State.PreviousSlot -and $State.PreviousSlot -notmatch $slotPattern){throw 'Previous slot id is invalid'}
}

function Get-DeploymentState([string]$DeploymentRoot,[string]$PublicKeyFile,[string]$ExpectedKeyId){
    $stateRoot=Join-Path $DeploymentRoot 'state'
    $currentFile=Join-Path $stateRoot 'current.json'
    $signatureFile=Join-Path $stateRoot 'current.json.sig'
    if(-not(Test-Path -LiteralPath $currentFile -PathType Leaf) -or
       -not(Test-Path -LiteralPath $signatureFile -PathType Leaf)){throw 'Signed deployment state is missing'}
    $bytes=[IO.File]::ReadAllBytes($currentFile)
    $signature=[IO.File]::ReadAllBytes($signatureFile)
    if(-not(Test-DeploymentStateSignature $bytes $signature $PublicKeyFile)){
        throw 'Deployment state signature is invalid'
    }
    try{$state=$script:DeploymentStateUtf8.GetString($bytes)|ConvertFrom-Json -Depth 12 -DateKind String}
    catch{throw 'Deployment state JSON is invalid'}
    $fingerprint=Get-RsaPublicFingerprint $PublicKeyFile
    Assert-DeploymentStateShape $state $ExpectedKeyId $fingerprint
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $auditRoot=Join-Path $stateRoot 'audit'
    $auditFiles=@(Get-ChildItem -LiteralPath $auditRoot -File -Filter '*.json'|Sort-Object Name)
    if($auditFiles.Count -ne [int64]$state.Generation){throw 'Deployment state audit chain is incomplete'}
    $previous=$null
    for($index=0;$index -lt $auditFiles.Count;$index++){
        $expectedGeneration=$index+1
        $auditBytes=[IO.File]::ReadAllBytes($auditFiles[$index].FullName)
        $auditHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($auditBytes)).ToLowerInvariant()
        if($auditFiles[$index].BaseName -cne ('{0:D8}-{1}' -f $expectedGeneration,$auditHash.Substring(0,16))){
            throw 'Deployment state audit filename is invalid'
        }
        $auditSignatureFile=$auditFiles[$index].FullName+'.sig'
        if(-not(Test-Path -LiteralPath $auditSignatureFile -PathType Leaf) -or
           -not(Test-DeploymentStateSignature $auditBytes ([IO.File]::ReadAllBytes($auditSignatureFile)) $PublicKeyFile)){
            throw 'Deployment state audit signature is invalid'
        }
        $audit=$script:DeploymentStateUtf8.GetString($auditBytes)|ConvertFrom-Json -Depth 12 -DateKind String
        Assert-DeploymentStateShape $audit $ExpectedKeyId $fingerprint
        if([int64]$audit.Generation -ne $expectedGeneration -or $audit.DeploymentId -cne $state.DeploymentId -or
           $audit.StateSignerKeyId -cne $ExpectedKeyId -or $audit.PreviousStateSha256 -cne $previous){
            throw 'Deployment state audit hash chain is invalid'
        }
        $previous=$auditHash
    }
    if($previous -cne $hash -or -not [Linq.Enumerable]::SequenceEqual([byte[]]$bytes,[byte[]][IO.File]::ReadAllBytes($auditFiles[-1].FullName))){
        throw 'Current deployment state is not the audit chain head'
    }
    return [pscustomobject]@{State=$state;Bytes=$bytes;Sha256=$hash;Root=$stateRoot}
}

function Save-DeploymentState([hashtable]$State,[string]$DeploymentRoot,[string]$PrivateKeyFile){
    $stateRoot=Join-Path $DeploymentRoot 'state';$auditRoot=Join-Path $stateRoot 'audit'
    New-Item -ItemType Directory -Path $auditRoot -Force|Out-Null
    $bytes=$script:DeploymentStateUtf8.GetBytes(($State|ConvertTo-Json -Depth 12))
    $rsa=[Security.Cryptography.RSA]::Create()
    try{
        $rsa.ImportFromPem([IO.File]::ReadAllText((Get-DeploymentFullPath $PrivateKeyFile)))
        if($rsa.KeySize -lt 3072){throw 'Deployment state RSA key must be at least 3072 bits'}
        $signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
    }
    finally{$rsa.Dispose()}
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $auditBase=Join-Path $auditRoot ('{0:D8}-{1}.json' -f [int64]$State.Generation,$hash.Substring(0,16))
    if(Test-Path -LiteralPath $auditBase){throw 'Deployment state generation already exists'}
    [IO.File]::WriteAllBytes($auditBase,$bytes);[IO.File]::WriteAllBytes($auditBase+'.sig',$signature)
    $tempJson=Join-Path $stateRoot ('current-'+[guid]::NewGuid().ToString('N')+'.json')
    $tempSig=$tempJson+'.sig'
    try{
        [IO.File]::WriteAllBytes($tempJson,$bytes);[IO.File]::WriteAllBytes($tempSig,$signature)
        [IO.File]::Move($tempJson,(Join-Path $stateRoot 'current.json'),$true)
        [IO.File]::Move($tempSig,(Join-Path $stateRoot 'current.json.sig'),$true)
    }
    finally{
        if(Test-Path -LiteralPath $tempJson){Remove-Item -LiteralPath $tempJson -Force}
        if(Test-Path -LiteralPath $tempSig){Remove-Item -LiteralPath $tempSig -Force}
    }
    return $hash
}

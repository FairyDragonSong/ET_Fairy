# Acceptance receiver only; no arbitrary forwarding, commands, or raw payload logging.
$ErrorActionPreference='Stop'
$root=$env:ET_ALERT_TEST_DIRECTORY
if(-not [IO.Path]::IsPathFullyQualified($root) -or (Split-Path $root -Leaf) -notmatch '^metrics-test-[a-f0-9]{32}$'){throw 'Invalid isolated alert fixture'}
$token=Get-Content (Join-Path $root 'alert-token.txt') -Raw
if($token -notmatch '^[A-F0-9]{64}$'){throw 'Invalid alert test token'}
$expected=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes("Bearer $token"))
$listener=[Net.HttpListener]::new();$listener.Prefixes.Add('http://127.0.0.1:30911/')
try {
    $listener.Start()
    while($true){
        $context=$listener.GetContext()
        try {
            $auth=$context.Request.Headers['Authorization']
            if(-not $auth -or $auth.Length -ne 71 -or -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($auth)),$expected)){
                $context.Response.StatusCode=401;continue
            }
            if($context.Request.HttpMethod -ne 'POST' -or $context.Request.RawUrl -ne '/alerts'){$context.Response.StatusCode=404;continue}
            if($context.Request.ContentLength64 -lt 0 -or $context.Request.ContentLength64 -gt 65536){$context.Response.StatusCode=413;continue}
            $reader=[IO.StreamReader]::new($context.Request.InputStream)
            try {$payload=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
            foreach($alert in $payload.alerts){
                if($alert.labels.alertname -ne 'ETLocalTargetDown' -or $alert.labels.job -notin @('et-primary','et-peer') -or $alert.status -notin @('firing','resolved')){throw 'Unexpected test alert'}
                $entry=[ordered]@{Status=$alert.status;Job=$alert.labels.job;Alertname=$alert.labels.alertname;Fingerprint=$alert.fingerprint;ReceivedAtUtc=[DateTime]::UtcNow.ToString('O')}
                [IO.File]::AppendAllText((Join-Path $root 'received-alerts.jsonl'),($entry|ConvertTo-Json -Compress)+[Environment]::NewLine)
            }
            $context.Response.StatusCode=200
        }catch {$context.Response.StatusCode=400}
        finally {$context.Response.Close()}
    }
}finally {$listener.Close()}

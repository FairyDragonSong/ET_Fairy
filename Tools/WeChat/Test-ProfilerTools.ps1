$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $workspace ('Temp/WeChatProfilerToolTests-' + [Guid]::NewGuid().ToString('N'))
$mini = Join-Path $fixture 'minigame'
New-Item -ItemType Directory -Force -Path $mini | Out-Null
'{"development":true,"autoConnect":true}' | Set-Content -LiteralPath (Join-Path $fixture 'profiler-build.json')
$framework = Join-Path $mini 'fixture.framework.js'
'function connect(url, opts) { return new WebSocketConstructor(url,opts); }' | Set-Content -LiteralPath $framework
& (Join-Path $PSScriptRoot 'Route-ProfilerConnection.ps1') -ExportRoot $fixture -HostAddress '192.168.1.8' -Port 55504 -OriginalPort 37100 | Out-Null
$nodeTest = Join-Path $fixture 'verify.cjs'
@'
const fs = require('fs'), vm = require('vm'), assert = require('assert');
const context = { console, WebSocketConstructor: function(url) {
  this.url = url; this.addEventListener = function() {};
} };
vm.createContext(context);
vm.runInContext(fs.readFileSync(process.argv[2], 'utf8'), context);
assert.equal(context.connect('ws://10.0.0.1:37100/path').url, 'ws://192.168.1.8:55504/path');
assert.equal(context.connect('ws://game.example:30300/path').url, 'ws://game.example:30300/path');
assert.equal(context.connect('wss://game.example/path').url, 'wss://game.example/path');
console.log('PASS profiler route: exact source port rewritten, gameplay sockets preserved');
'@ | Set-Content -LiteralPath $nodeTest -Encoding utf8
& node $nodeTest $framework
if ($LASTEXITCODE -ne 0) { throw 'JS route test failed.' }
$rejected = $false
try { & (Join-Path $PSScriptRoot 'Route-ProfilerConnection.ps1') -ExportRoot $fixture -HostAddress '192.168.1.8' -Port 55504 -OriginalPort 37100 | Out-Null }
catch { $rejected = $_.Exception.Message -like '*Already routed*' }
if (!$rejected) { throw 'Expected double-patch rejection.' }
$record = Get-Content -LiteralPath (Join-Path $fixture 'profiler-route.json') -Raw | ConvertFrom-Json
if ($record.originalSha256 -eq $record.routedSha256) { throw 'Expected changed hash.' }
Write-Output "PASS backup, route manifest, double-patch protection; fixture=$fixture"
$parseErrors = $null
$tokens = $null
[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-ProfilerConnection.ps1'), [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
Write-Output 'PASS connection doctor parses'

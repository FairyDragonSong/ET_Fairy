param(
    [Parameter(Mandatory)][string]$ExportRoot,
    [Parameter(Mandatory)][int]$ResourcePort,
    [string]$HostAddress = '192.168.1.8',
    [switch]$ObserveFrameLoop,
    [switch]$SimulatorDeadline
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exportPath = [IO.Path]::GetFullPath([IO.Path]::Combine($workspace, $ExportRoot))
if (!$exportPath.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Export outside workspace' }
$manifestPath = Join-Path $exportPath 'profiler-build.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (!$manifest.cpuVariant) { throw 'Expected CPU experiment manifest' }
$evidence = Join-Path $exportPath 'profiler-build-evidence'
$logs = Join-Path $workspace ('Temp/WeChatCpuExperiments/postprocess/' + $manifest.cpuVariant)
New-Item -ItemType Directory -Path $logs -Force | Out-Null
foreach ($relative in @('Unity/Temp/BurstOutput/Build/lib_burst_generated.txt','Unity/Temp/BurstOutput/Plugins/lib_burst_generated.cpp')) {
    $sourcePath = Join-Path $workspace $relative
    if (Test-Path -LiteralPath $sourcePath) { Copy-Item -LiteralPath $sourcePath -Destination $evidence }
}
$sdk = 'D:/Program Files/TuanJieEdit/2022.3.62t11/Editor/Data/PlaybackEngines/WeixinMiniGameSupport/BuildTools/Emscripten'
& (Join-Path $sdk 'llvm/llvm-objdump.exe') --disassemble --no-show-raw-insn (Join-Path $evidence 'lib_burst_generated.a') > (Join-Path $evidence 'burst-disassembly.txt')
if ($LASTEXITCODE -ne 0) { throw 'Burst disassembly failed' }
& python (Join-Path $PSScriptRoot 'Inspect-BurstVectorization.py') $evidence
if ($LASTEXITCODE -ne 0) { throw 'Burst opcode inspection failed' }
if (!$manifest.wasmCompatibility) {
    $optimized = Join-Path $logs 'optimized.wasm'
    $optimizerArgs = @((Join-Path $exportPath 'webgl/Build/webgl.wasm'), '-Oz','--strip-debug','--enable-sign-ext','--enable-nontrapping-float-to-int','--enable-bulk-memory')
    if ($manifest.simd) { $optimizerArgs += '--enable-simd' }
    if ($manifest.threads) { $optimizerArgs += '--enable-threads' }
    $optimizerArgs += @('-o', $optimized)
    & (Join-Path $sdk 'binaryen/bin/wasm-opt.exe') @optimizerArgs *> (Join-Path $logs 'wasm-opt.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $logs 'wasm-opt.log') -Tail 10; throw 'wasm-opt failed' }
    & node (Join-Path $PSScriptRoot 'Apply-WasmCompatibility.cjs') $exportPath $optimized *> (Join-Path $logs 'apply.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $logs 'apply.log') -Tail 10; throw 'Wasm compatibility apply failed' }
}
@'
import json,sys
from pathlib import Path
p=Path(sys.argv[1]);m=json.loads((p/'profiler-build.json').read_text(encoding='utf-8-sig'))
cfg=p/'minigame/project.config.json';r=json.loads(cfg.read_text(encoding='utf-8-sig'));r['projectname']='RedBlue-CPU-'+m['cpuVariant'];cfg.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
if m['simd']:
 f=p/'minigame/game.json';r=json.loads(f.read_text(encoding='utf-8-sig'));r['useWasmBuffer']=True;f.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
'@ | python - $exportPath
if ($LASTEXITCODE -ne 0) { throw 'Project configuration failed' }
& (Join-Path $PSScriptRoot 'Start-ProfilerSession.ps1') -ExportRoot $ExportRoot -HostAddress $HostAddress -ResourcePort $ResourcePort -AllowRelease:(!$manifest.development)
if ($manifest.development -and !(Test-Path -LiteralPath (Join-Path $exportPath 'profiler-route.json'))) {
    & (Join-Path $PSScriptRoot 'Route-ProfilerConnection.ps1') -ExportRoot $ExportRoot -HostAddress $HostAddress -Port 37100 -OriginalPort 37100
}
if ($manifest.threads) {
    $diagnosticsPath = Join-Path $exportPath 'startup-diagnostics.json'
    if (!(Test-Path -LiteralPath $diagnosticsPath) -or (Get-Content -LiteralPath $diagnosticsPath -Raw | ConvertFrom-Json).diagnosticVersion -lt 5) {
        & node (Join-Path $PSScriptRoot 'Add-StartupDiagnostics.cjs') $exportPath
        if ($LASTEXITCODE -ne 0) { throw 'Startup diagnostics installation failed' }
    }
    & node (Join-Path $PSScriptRoot 'Apply-SharedBufferCompatibility.cjs') $exportPath
    if ($LASTEXITCODE -ne 0) { throw 'Shared buffer compatibility patch failed' }
    & node --check (Join-Path $exportPath 'minigame/webgl.wasm.framework.unityweb.js')
    if ($LASTEXITCODE -ne 0) { throw 'Startup diagnostics JS syntax failed' }
}
$cacheArgs = @($exportPath)
if (!$manifest.development) { $cacheArgs += '--allow-release' }
& node (Join-Path $PSScriptRoot 'Apply-ExperimentCacheGuard.cjs') @cacheArgs
if ($LASTEXITCODE -ne 0) { throw 'Experiment cache guard failed' }
if ($ObserveFrameLoop) {
    & node (Join-Path $PSScriptRoot 'Add-FrameLoopDiagnostics.cjs') $exportPath
    if ($LASTEXITCODE -ne 0) { throw 'Frame-loop observer failed' }
}
if ($SimulatorDeadline) {
    & node (Join-Path $PSScriptRoot 'Apply-SimulatorDeadline.cjs') $exportPath
    if ($LASTEXITCODE -ne 0) { throw 'Simulator deadline patch failed' }
}
$httpArgs = @($exportPath, '--output', (Join-Path $evidence 'compatibility-http-validation.json'))
if (!$manifest.development) { $httpArgs += '--allow-release' }
& python (Join-Path $PSScriptRoot 'Verify-ProfilerExport.py') @httpArgs
if ($LASTEXITCODE -ne 0) { throw 'HTTP validation failed' }
Write-Output ('CPU experiment ready: ' + $exportPath)

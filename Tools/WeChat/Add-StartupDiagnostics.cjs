// Observe the first UnityModule failure before the SDK retries with a mutated Module.
const fs = require('fs'), path = require('path'), crypto = require('crypto');
const root = path.resolve(process.argv[2]);
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'profiler-build.json'), 'utf8').replace(/^\uFEFF/, ''));
const session = JSON.parse(fs.readFileSync(path.join(root, 'profiler-session.json'), 'utf8').replace(/^\uFEFF/, ''));
const file = path.join(root, 'minigame/webgl.wasm.framework.unityweb.js');
let source = fs.readFileSync(file, 'utf8');
if (source.includes('REDBLUE_STARTUP_DIAGNOSTICS_V5')) throw new Error('Startup diagnostics already applied');
const previousMarker = source.search(/;\/\* REDBLUE_STARTUP_DIAGNOSTICS_V[1234] \*\//);
if (previousMarker >= 0) source = source.slice(0, previousMarker);
if (!source.includes('GameGlobal.unityNamespace.UnityModule =')) throw new Error('SDK UnityModule export changed');
const backup = path.join(root, 'profiler-backups/framework-before-startup-diagnostics.js');
if (fs.existsSync(backup) && previousMarker < 0) throw new Error('Backup exists');
const hook = `
;/* REDBLUE_STARTUP_DIAGNOSTICS_V5 */
(function(){
 var original = GameGlobal.unityNamespace.UnityModule, attempt = 0;
 var sharedMemory = {};
 ['getSharedArrayBuffer','getAtomics'].forEach(function(name){
  sharedMemory[name+'Api'] = typeof wx[name];
  if(typeof wx[name] === 'function') {
   try {var value = wx[name](); sharedMemory[name+'Result'] = typeof value;}
   catch(e){sharedMemory[name+'Error'] = String(e);}
  }
 });
 try {
  var probe = new WebAssembly.Memory({initial:1,maximum:1,shared:true});
  sharedMemory.bufferTag = Object.prototype.toString.call(probe.buffer);
  sharedMemory.bufferConstructor = probe.buffer.constructor && probe.buffer.constructor.name;
  sharedMemory.bufferIsArrayBuffer = probe.buffer instanceof ArrayBuffer;
 } catch(e){sharedMemory.probeError = String(e);}

 function report(error, n, phase) {
  var system = {}; try { system = wx.getSystemInfoSync(); } catch(ignored) {}
  var record = {schema:1, kind:'startup', buildId:${JSON.stringify(manifest.buildId)}, attempt:n,
   diagnosticVersion:5, phase:phase || 'module-error', platform:system.platform || '',
   wechatVersion:system.version || '', sdkVersion:system.SDKVersion || '',
   system:system.system || '',
   standardWorkerSupport:wx.env && typeof wx.env.isSupportStandardWorker === 'boolean' ? wx.env.isSupportStandardWorker : null,
   standardWorkerSupportType:typeof (wx.env && wx.env.isSupportStandardWorker),
   message:String(error), stack:error && error.stack ? String(error.stack) : '',
   sharedMemory:sharedMemory, sharedArrayBuffer:typeof SharedArrayBuffer, atomics:typeof Atomics, wasm:typeof WebAssembly, wxWasm:typeof WXWebAssembly};
  console.info('REDBLUE_STARTUP_DIAGNOSTIC:', JSON.stringify(record));
  try { wx.request({url:${JSON.stringify(session.resourceUrl + '/diagnostic')},method:'POST',data:record,timeout:10000}); } catch(ignored) {}
 }
 GameGlobal.redBlueStartupReport = report;
 report('framework loaded', 0, 'framework-loaded');
 GameGlobal.unityNamespace.UnityModule = function(module) {
  var n = ++attempt;
  report('UnityModule invoked', n, 'module-invoked');
  var timer = setTimeout(function(){report('UnityModule still pending after 10 seconds',n,'module-pending');},10000);
  try {
   var result = original(module);
   return result && typeof result.then === 'function' ? result.then(function(value){clearTimeout(timer);report('UnityModule ready',n,'module-ready');return value;},function(e){clearTimeout(timer);report(e,n);throw e;}) : (clearTimeout(timer),result);
  } catch(e) {clearTimeout(timer);report(e,n);throw e;}
 };
})();
`;
if (!fs.existsSync(backup)) fs.copyFileSync(file, backup);
fs.writeFileSync(file, source + hook);
const gameFile = path.join(root, 'minigame/game.js');
let game = fs.readFileSync(gameFile, 'utf8');
if (!game.includes('REDBLUE_STARTUP_PHASES_V2')) {
 const gameBackup = path.join(root, 'profiler-backups/game-before-startup-phases.js');
 if (fs.existsSync(gameBackup)) throw new Error('Game startup phase backup exists');
 fs.copyFileSync(gameFile, gameBackup);
 const entry = `/* REDBLUE_STARTUP_PHASES_V2 */
if (GameGlobal.redBlueStartupReport) {
 GameGlobal.redBlueStartupReport('game entry',0,'game-entry');
 wx.onError(function(e){GameGlobal.redBlueStartupReport(e,0,'wx-error');});
 if (wx.onUnhandledRejection) wx.onUnhandledRejection(function(e){GameGlobal.redBlueStartupReport(e.reason,0,'wx-unhandled-rejection');});
}
`;
 if (!game.includes('const managerConfig = {') || !game.includes('gameManager.onLaunchProgress((e) => {')) throw new Error('SDK startup structure changed');
 game = game.replace('const managerConfig = {', entry + 'const managerConfig = {')
  .replace('gameManager.onLaunchProgress((e) => {', `gameManager.onLaunchProgress((e) => {
            if(GameGlobal.redBlueStartupReport) GameGlobal.redBlueStartupReport('launch progress '+e.type,0,'launch-'+e.type);`)
  .replace('gameManager.onLogError = function (err) {', `gameManager.onLogError = function (err) {
            if(GameGlobal.redBlueStartupReport) GameGlobal.redBlueStartupReport(err,0,'manager-error');`);
 fs.writeFileSync(gameFile, game);
}
if (!game.includes('REDBLUE_PLUGIN_IMPORT_DIAGNOSTICS_V1')) {
 if (!game.includes("UnityManager = requirePlugin('UnityPlugin', {") || !game.includes('            }).default;')) throw new Error('SDK plugin import structure changed');
 const pluginBackup = path.join(root, 'profiler-backups/game-before-plugin-import-diagnostics.js');
 if (fs.existsSync(pluginBackup)) throw new Error('Plugin diagnostics backup exists');
 fs.copyFileSync(gameFile, pluginBackup);
 game = game.replace("UnityManager = requirePlugin('UnityPlugin', {", "/* REDBLUE_PLUGIN_IMPORT_DIAGNOSTICS_V1 */\n            const pluginExport = requirePlugin('UnityPlugin', {")
  .replace('            }).default;', `            });
            if(GameGlobal.redBlueStartupReport) GameGlobal.redBlueStartupReport(JSON.stringify({moduleType:typeof pluginExport, keys:Object.keys(pluginExport || {}).slice(0,10), defaultType:typeof (pluginExport && pluginExport.default)}),0,'plugin-export');
            UnityManager = pluginExport.default;`)
  .replace('console.error(\'requirePlugin:\', error);', `console.error('requirePlugin:', error);
            if(GameGlobal.redBlueStartupReport) GameGlobal.redBlueStartupReport(error,0,'require-plugin-error');
            throw error;`);
 fs.writeFileSync(gameFile, game);
}
const sha = f => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
fs.writeFileSync(path.join(root, 'startup-diagnostics.json'), JSON.stringify({schema:1,diagnosticVersion:5,buildId:manifest.buildId,file,backup,originalSha256:sha(backup),instrumentedSha256:sha(file),gameJsSha256:sha(gameFile),url:session.resourceUrl+'/diagnostic',note:'Startup-only observer; preserves exceptions and SDK retry behavior; simulation unchanged; plugin import failures stop instead of producing a secondary constructor error'},null,2)+'\n');
console.log('Startup error observer installed:', manifest.buildId);

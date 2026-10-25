// Keep a 60Hz monotonic cadence in devtools, including frames that overrun one interval.
const fs = require('fs'), path = require('path'), crypto = require('crypto'), vm = require('vm');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

function patchSource(source) {
  const start = source.indexOf('function _emscripten_set_main_loop_timing(mode,value){');
  if (start < 0 || source.indexOf('function _emscripten_set_main_loop_timing(mode,value){', start + 1) >= 0)
    throw new Error('Unexpected main-loop timing function');
  const anchor = 'if(!Browser.mainLoop.func){return 1}';
  const offset = source.indexOf(anchor, start);
  if (offset < start || offset - start > 2000) throw new Error('Unexpected timing setup');
  const originalTimer = 'var timeUntilNextTick=Math.max(0,Browser.mainLoop.tickStartTime+value-_emscripten_get_now())|0;setTimeout(Browser.mainLoop.runner,timeUntilNextTick)';
  const timer = source.indexOf(originalTimer, offset);
  if (timer < offset || timer - offset > 2000 || source.split(originalTimer).length !== 2)
    throw new Error('Existing compensated timer not found');
  const hook = `/* REDBLUE_SIMULATOR_DEADLINE_V3 */
Browser.mainLoop.redBlueCadence60=false;
try{
 if(mode===1 && value===1 && wx.getSystemInfoSync().platform==='devtools'){
  wx.setPreferredFramesPerSecond(60);
  mode=0;value=1000/60;Browser.mainLoop.timingMode=mode;Browser.mainLoop.timingValue=value;
  Browser.mainLoop.redBlueCadence60=true;Browser.mainLoop.redBlueNextDeadline=_emscripten_get_now();
  if(!Browser.mainLoop.redBlueRafPump){
   Browser.mainLoop.redBlueRafPump=true;
   requestAnimationFrame(function redBluePump(){
    if(Browser.mainLoop.redBlueCadence60)requestAnimationFrame(redBluePump);
    else Browser.mainLoop.redBlueRafPump=false;
   });
  }
 }
}catch(ignored){}
`;
  const cadenceTimer = `var timeUntilNextTick;
if(Browser.mainLoop.redBlueCadence60){
 var redBlueNow=_emscripten_get_now(),redBlueDeadline=Browser.mainLoop.redBlueNextDeadline;
 if(redBlueDeadline<redBlueNow-100 || redBlueDeadline>redBlueNow+100)redBlueDeadline=redBlueNow;
 redBlueDeadline+=value;Browser.mainLoop.redBlueNextDeadline=redBlueDeadline;
 timeUntilNextTick=Math.max(0,redBlueDeadline-redBlueNow)|0;
}else{timeUntilNextTick=Math.max(0,Browser.mainLoop.tickStartTime+value-_emscripten_get_now())|0;}
setTimeout(Browser.mainLoop.runner,timeUntilNextTick)`;
  const quietStartup = `
;/* REDBLUE_SIMULATOR_QUIET_SUGGESTIONS_V1 */
try{if(wx.getSystemInfoSync().platform==='devtools')GameGlobal.unityNamespace.monitorConfig.showSuggestModal=false;}catch(ignored){}
`;
  return (source.slice(0, offset) + hook + source.slice(offset)).replace(originalTimer, cadenceTimer) + quietStartup;
}

function apply(root, rollback=false) {
  const file=path.join(root,'minigame/webgl.wasm.framework.unityweb.js');
  const info=path.join(root,'simulator-deadline.json');
  const backup=path.join(root,'profiler-backups/framework-before-simulator-deadline.js');
  if(fs.existsSync(info)){
    const record=JSON.parse(fs.readFileSync(info,'utf8'));
    if(sha(fs.readFileSync(file))!==record.afterSha256)throw new Error('Framework changed after deadline patch');
    if(rollback){
      if(sha(fs.readFileSync(backup))!==record.beforeSha256)throw new Error('Backup changed');
      fs.copyFileSync(backup,file);fs.unlinkSync(info);
    }
    return;
  }
  if(rollback)throw new Error('Deadline patch not applied');
  const before=fs.readFileSync(file),after=patchSource(before.toString('utf8'));
  new vm.Script(after);
  fs.mkdirSync(path.dirname(backup),{recursive:true});
  if(fs.existsSync(backup)&&sha(fs.readFileSync(backup))!==sha(before))throw new Error('Different backup exists');
  fs.writeFileSync(backup,before);fs.writeFileSync(file,after);
  fs.writeFileSync(info,JSON.stringify({schema:1,version:3,beforeSha256:sha(before),afterSha256:sha(Buffer.from(after)),
    note:'Devtools 60Hz only: monotonic deadlines with 100ms maximum catch-up lag, preferred FPS 60, empty rAF pump to keep the simulator frame path active; suppress SDK suggestion dialogs. Phone and non-60Hz timing unchanged. No Wasm or simulation changes.'},null,2)+'\n');
}
if(require.main===module)apply(path.resolve(process.argv[2]),process.argv.includes('--rollback'));
module.exports={patchSource,apply};

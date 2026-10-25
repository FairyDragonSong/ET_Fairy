// Export-only observer. It leaves the engine's scheduler and simulation unchanged.
const fs = require('fs'), path = require('path'), crypto = require('crypto'), vm = require('vm');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const anchor = 'Browser.mainLoop.runIter(browserIterationFunc);';

function instrument(source, buildId, url) {
  if (source.split(anchor).length !== 2) throw new Error('Expected exactly one engine iteration');
  const observer = `
;/* REDBLUE_FRAME_LOOP_DIAGNOSTICS_V1 */
(function(){
 var start=0,previous=0,count=0,work=0,interval=0,maxWork=0,maxInterval=0,failures=0;
 GameGlobal.redBlueFrameLoopSample=function(begin,end,loop,failed){
  try {
   if(!start) start=begin;
   if(previous){interval+=begin-previous;count++;work+=end-begin;maxWork=Math.max(maxWork,end-begin);maxInterval=Math.max(maxInterval,begin-previous);}
   previous=begin;if(failed)failures++;
   if(end-start<5000 || !count)return;
   var record={schema:1,kind:'startup',phase:'frame-loop',buildId:${JSON.stringify(buildId)},
    frames:count,seconds:(end-start)/1000,fps:count*1000/interval,
    engineMsPerFrame:work/count,intervalMsPerFrame:interval/count,maxEngineMs:maxWork,maxIntervalMs:maxInterval,
    timingMode:loop.timingMode,timingValue:loop.timingValue,method:loop.method,failures:failures,
    visible:GameGlobal.manager.isVisible,note:'Engine runIter observer; no scheduler changes. Not Unity Profiler frames.'};
   start=end;count=work=interval=maxWork=maxInterval=failures=0;
   console.info('REDBLUE_FRAME_LOOP:',JSON.stringify(record));
   wx.request({url:${JSON.stringify(url)},method:'POST',data:record,timeout:10000});
  }catch(ignored){}
 };
})();
`;
  return source.replace(anchor, `var redBlueIterationStart=performance.now(),redBlueIterationFailed=true;
try{${anchor}redBlueIterationFailed=false;}
finally{GameGlobal.redBlueFrameLoopSample(redBlueIterationStart,performance.now(),Browser.mainLoop,redBlueIterationFailed);}`) + observer;
}

function apply(root, rollback = false) {
  const file = path.join(root, 'minigame/webgl.wasm.framework.unityweb.js');
  const info = path.join(root, 'frame-loop-diagnostics.json');
  const backup = path.join(root, 'profiler-backups/framework-before-frame-loop-diagnostics.js');
  if (fs.existsSync(info)) {
    const record = JSON.parse(fs.readFileSync(info, 'utf8'));
    if (sha(fs.readFileSync(file)) !== record.afterSha256) throw new Error('Framework changed after diagnostics');
    if (rollback) {
      if (sha(fs.readFileSync(backup)) !== record.beforeSha256) throw new Error('Backup changed');
      fs.copyFileSync(backup, file);fs.unlinkSync(info);
    }
    return;
  }
  if (rollback) throw new Error('Diagnostics not applied');
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'profiler-build.json'), 'utf8').replace(/^\uFEFF/, ''));
  const session = JSON.parse(fs.readFileSync(path.join(root, 'profiler-session.json'), 'utf8').replace(/^\uFEFF/, ''));
  const before = fs.readFileSync(file), after = instrument(before.toString('utf8'), manifest.buildId, session.resourceUrl + '/diagnostic');
  new vm.Script(after);
  fs.mkdirSync(path.dirname(backup), {recursive:true});
  if (fs.existsSync(backup) && sha(fs.readFileSync(backup)) !== sha(before)) throw new Error('Different backup exists');
  fs.writeFileSync(backup, before);fs.writeFileSync(file, after);
  fs.writeFileSync(info, JSON.stringify({schema:1,buildId:manifest.buildId,beforeSha256:sha(before),afterSha256:sha(Buffer.from(after)),
    note:'Export-only engine iteration observer; no timing/scheduler or Wasm changes.'},null,2)+'\n');
}

if (require.main === module) apply(path.resolve(process.argv[2]), process.argv.includes('--rollback'));
module.exports = {instrument,apply};

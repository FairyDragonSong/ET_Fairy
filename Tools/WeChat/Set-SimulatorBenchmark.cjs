// Temporary export-only test launcher; invokes the existing Unity benchmark methods.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
function set(root, command) {
  const file = path.join(root, 'minigame/game.js');
  const backup = path.join(root, 'profiler-backups/game-before-simulator-benchmark.js');
  const info = path.join(root, 'simulator-benchmark.json');
  if (fs.existsSync(info)) {
    const previous = JSON.parse(fs.readFileSync(info, 'utf8'));
    if (sha(fs.readFileSync(file)) !== previous.afterSha256 || sha(fs.readFileSync(backup)) !== previous.beforeSha256)
      throw new Error('Launcher or backup changed');
    fs.copyFileSync(backup, file); fs.unlinkSync(info);
  }
  if (command === 'rollback') return;
  const method = {frame:'StartFrameBenchmark', cpu:'StartCpuBenchmark', sim:'Press'}[command];
  const gameObject = command === 'sim' ? '50k Islands sim' : 'ET RedBlue Demo';
  if (!method) throw new Error('Expected frame, cpu, sim or rollback');
  const before = fs.readFileSync(file);
  if (fs.existsSync(backup) && sha(fs.readFileSync(backup)) !== sha(before)) throw new Error('Different backup exists');
  const hook = `
;/* REDBLUE_SIMULATOR_BENCHMARK_V1 */
if(wx.getSystemInfoSync().platform==='devtools'){
 wx.onTouchStart(e=>console.info('REDBLUE_AUTOMATION_TOUCH',JSON.stringify({touches:e.changedTouches,width:window.innerWidth,height:window.innerHeight,pixelRatio:window.devicePixelRatio})));
 let redBlueLaunchWait=0;
 const redBlueLaunchTimer=setInterval(()=>{
  if(typeof GameGlobal.Module?.SendMessage!=='function')return;
  if(++redBlueLaunchWait<10)return;
  clearInterval(redBlueLaunchTimer);
  console.info('REDBLUE_AUTOMATION_START',${JSON.stringify(method)});
  GameGlobal.Module.SendMessage(${JSON.stringify(gameObject)},${JSON.stringify(method)});
 },1000);
}
`;
  const after = Buffer.concat([before, Buffer.from(hook)]);
  fs.mkdirSync(path.dirname(backup), {recursive:true}); fs.writeFileSync(backup, before);
  fs.writeFileSync(file, after);
  fs.writeFileSync(info, JSON.stringify({schema:1,command,gameObject,method,beforeSha256:sha(before),afterSha256:sha(after),
    note:'Devtools-only delayed invocation of the existing benchmark; no simulation or timing changes.'},null,2)+'\n');
}
if(require.main===module)set(path.resolve(process.argv[2]),process.argv[3]);
module.exports={set};

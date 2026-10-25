const test=require('node:test'),assert=require('node:assert/strict'),vm=require('node:vm');
const {patchSource}=require('./Apply-SimulatorDeadline.cjs');
const {apply}=require('./Apply-SimulatorDeadline.cjs');
const fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const source=`function _emscripten_set_main_loop_timing(mode,value){
Browser.mainLoop.timingMode=mode;Browser.mainLoop.timingValue=value;
if(!Browser.mainLoop.func){return 1}
if(mode==0){Browser.mainLoop.scheduler=function(){var timeUntilNextTick=Math.max(0,Browser.mainLoop.tickStartTime+value-_emscripten_get_now())|0;setTimeout(Browser.mainLoop.runner,timeUntilNextTick)};Browser.mainLoop.method='timeout'}
else if(mode==1){Browser.mainLoop.method='rAF'}return 0;}`;

test('compensates work only for devtools 60Hz and keeps actual original runner',()=>{
  for(const [platform,mode,value,expected] of [['devtools',1,1,'timeout'],['android',1,1,'rAF'],['ios',1,1,'rAF'],['devtools',1,2,'rAF']]){
    let scheduled,preferredFps,raf;
    const runner=()=>{};
    const c={GameGlobal:{unityNamespace:{monitorConfig:{showSuggestModal:true}}},wx:{getSystemInfoSync:()=>({platform}),setPreferredFramesPerSecond:fps=>preferredFps=fps},Browser:{mainLoop:{func:()=>{},runner,tickStartTime:100}},_emscripten_get_now:()=>107,setTimeout:(func,delay)=>scheduled={func,delay},requestAnimationFrame:fn=>raf=fn};
    vm.createContext(c);vm.runInContext(patchSource(source),c);
    assert.equal(c._emscripten_set_main_loop_timing(mode,value),0);
    assert.equal(c.Browser.mainLoop.method,expected);
    if(expected==='timeout'){
      c.Browser.mainLoop.scheduler();assert.equal(scheduled.func,runner);assert.equal(scheduled.delay,16);
      assert.equal(c.Browser.mainLoop.timingMode,0);assert.equal(c.Browser.mainLoop.timingValue,1000/60);
      assert.equal(preferredFps,60);
      assert.equal(typeof raf,'function');
      const previous=raf;raf=null;previous();assert.equal(raf,previous);
      assert.equal(scheduled.func,runner,'rAF pump does not run simulation');
      c._emscripten_set_main_loop_timing(0,20);raf();assert.equal(c.Browser.mainLoop.redBlueRafPump,false);
    }else {assert.equal(c.Browser.mainLoop.timingMode,1);assert.equal(raf,undefined);}
    assert.equal(c.GameGlobal.unityNamespace.monitorConfig.showSuggestModal,platform!=='devtools');
  }
});

test('alternating 20ms work and idle frames sustain 60Hz without cumulative drift',()=>{
  let now=0,pending;
  const starts=[];
  const c={wx:{getSystemInfoSync:()=>({platform:'devtools'}),setPreferredFramesPerSecond:()=>{}},
    Browser:{mainLoop:{func:()=>{},tickStartTime:0}},_emscripten_get_now:()=>now,
    setTimeout:(func,delay)=>pending={func,delay:Math.max(1,delay)},requestAnimationFrame:()=>{}};
  c.Browser.mainLoop.runner=()=>{starts.push(now);now+=starts.length%2===1?20:0;c.Browser.mainLoop.scheduler();};
  vm.createContext(c);vm.runInContext(patchSource(source),c);
  c._emscripten_set_main_loop_timing(1,1);c.Browser.mainLoop.scheduler();
  for(let i=0;i<600;i++){now+=pending.delay;pending.func();}
  const hz=(starts.length-1)*1000/(starts.at(-1)-starts[0]);
  assert.ok(hz>59.8 && hz<60.2,`Actual callback rate ${hz}`);
  now+=1000;c.Browser.mainLoop.scheduler();
  assert.ok(pending.delay>=15,'Long pauses reset the deadline instead of replaying an unbounded backlog');
  c.Browser.mainLoop.tickStartTime=now;c._emscripten_set_main_loop_timing(0,20);now+=7;c.Browser.mainLoop.scheduler();
  assert.equal(pending.delay,13,'Other timer modes keep original work compensation');
});
test('unknown generated code is rejected',()=>assert.throws(()=>patchSource('changed')));
test('real file patch is idempotent, rejects intervening changes and rolls back exactly',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'redblue-deadline-'));
  try{
    fs.mkdirSync(path.join(root,'minigame'));
    const file=path.join(root,'minigame/webgl.wasm.framework.unityweb.js');
    fs.writeFileSync(file,source);
    apply(root);const patched=fs.readFileSync(file);apply(root);
    assert.deepEqual(fs.readFileSync(file),patched);
    fs.appendFileSync(file,'\n// changed');assert.throws(()=>apply(root,true));
    fs.writeFileSync(file,patched);apply(root,true);assert.equal(fs.readFileSync(file,'utf8'),source);
    apply(root);assert.deepEqual(fs.readFileSync(file),patched);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});

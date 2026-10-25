const test = require('node:test'), assert = require('node:assert/strict'), vm = require('node:vm');
const {instrument} = require('./Add-FrameLoopDiagnostics.cjs');

test('observes real iteration cost without changing iteration, error, or scheduler', () => {
  let now=1,calls=0,scheduled=0,fail=false;
  const originalError=new Error('original');
  const reports=[];
  const context={GameGlobal:{manager:{isVisible:true}},performance:{now:()=>now},console:{info:()=>{}},
    wx:{request:r=>reports.push(r.data)},browserIterationFunc:()=>{},
    Browser:{mainLoop:{timingMode:1,timingValue:1,method:'rAF',runIter:()=>{calls++;now+=7;if(fail)throw originalError;}}}};
  vm.createContext(context);
  vm.runInContext(instrument('function frame(){Browser.mainLoop.runIter(browserIterationFunc);scheduled();}', 'test', 'http://test/diagnostic'), context);
  context.scheduled=()=>scheduled++;
  for(let i=0;i<320;i++){context.frame();now+=10;}
  assert.equal(calls,320);assert.equal(scheduled,320);
  assert.equal(reports.length,1);assert.equal(reports[0].engineMsPerFrame,7);
  assert.equal(reports[0].intervalMsPerFrame,17);assert.equal(reports[0].method,'rAF');
  fail=true;assert.throws(()=>context.frame(),e=>e===originalError);
  assert.equal(scheduled,320);
  context.wx.request=()=>{throw new Error('observer transport');};fail=false;
  now+=6000;assert.doesNotThrow(()=>context.frame());
});

test('rejects unknown generated framework structure',()=>{
  assert.throws(()=>instrument('different','test','http://test'));
});

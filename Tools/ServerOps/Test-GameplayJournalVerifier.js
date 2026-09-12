const fs=require('fs'), vm=require('vm'), assert=require('assert');
const source=fs.readFileSync(require('path').join(__dirname,'Verify-GameplayJournal.js'),'utf8');
function run(change, shouldPass) {
    const fixture={accounts:[{_id:'1',LoginName:'e2e_0123456789abcdefp0',StateVersion:0}],
        records:[{AccountId:'1',AccountStateVersion:0,GateInstanceId:'0123456789abcdef0123456789abcdef',State:'ended',OpenedAtUtc:new Date(0),EndedAtUtc:new Date(1),DisconnectError:0}]};
    change(fixture);
    let result, exitCode=0;
    const context={Date,process:{env:{ET_GAMEPLAY_ACCOUNT:'e2e_0123456789abcdef',ET_GAMEPLAY_CLIENTS:'1',ET_MONGO_DATABASE:'ET_LOCAL_GAMEPLAY'}},
        print:line=>{result=JSON.parse(line);},quit:code=>{exitCode=code;},
        db:{adminCommand:()=>({setName:'et-local-rs'}),getSiblingDB:()=>({getCollection:name=>({aggregate:()=>({toArray:()=>name==='ET.Server.Account'?fixture.accounts:fixture.records})})})}};
    vm.runInNewContext(source,context);
    assert.equal(result.Passed,shouldPass); assert.equal(exitCode,shouldPass?0:2);
    assert.equal(result.ReadOnly,true);
}
run(()=>{},true);
run(f=>{f.accounts=[];f.records=[];},false);
run(f=>{f.records=[];},false);
run(f=>{f.records.push({...f.records[0]});},false);
run(f=>{f.records[0].State='admitted';},false);
run(f=>{delete f.records[0].EndedAtUtc;},false);
run(f=>{f.records[0].EndedAtUtc=new Date(-1);},false);
run(f=>{f.records[0].AccountStateVersion=1;},false);
run(f=>{delete f.records[0].DisconnectError;},false);
run(f=>{delete f.records[0].GateInstanceId;},false);
run(f=>{f.records[0].GateInstanceId='invalid';},false);
console.log('PASS journal verifier: valid cohort and 10 negative cases');

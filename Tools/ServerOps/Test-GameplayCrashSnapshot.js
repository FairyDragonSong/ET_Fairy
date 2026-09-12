const fs=require('fs'),vm=require('vm'),assert=require('assert'),crypto=require('crypto');
const source=fs.readFileSync(require('path').join(__dirname,'Snapshot-GameplayJournal.js'),'utf8');
function run(change,pass){
    const fixture={accounts:[{_id:'1'}],rows:[{_id:'record',AccountId:'1',State:'admitted',GateInstanceId:'0123456789abcdef0123456789abcdef'}]};
    change(fixture);let output,exit=0;
    vm.runInNewContext(source,{require:name=>{assert.equal(name,'crypto');return crypto;},EJSON:{stringify:JSON.stringify},
        process:{env:{ET_MONGO_DATABASE:'ET_LOCAL_GAMEPLAY',ET_GAMEPLAY_ACCOUNT:'e2e_0123456789abcdef',ET_GAMEPLAY_CLIENTS:'1'}},
        db:{adminCommand:()=>({setName:'et-local-rs'}),getSiblingDB:()=>({getCollection:name=>({aggregate:()=>({toArray:()=>name==='ET.Server.Account'?fixture.accounts:fixture.rows})})})},
        print:line=>{output=JSON.parse(line);},quit:code=>{exit=code;}});
    assert.equal(output.Passed,pass);assert.equal(exit,pass?0:2);assert.equal(output.ReadOnly,true);
    return output.SnapshotSha256;
}
const first=run(()=>{},true);assert.equal(first,run(()=>{},true));
assert.notEqual(first,run(f=>{f.rows[0].extra='changed';},true));
run(f=>{f.accounts=[];f.rows=[];},false);
run(f=>{f.rows=[];},false);
run(f=>{f.rows.push({...f.rows[0]});},false);
run(f=>{f.rows[0].State='ended';},false);
run(f=>{f.rows[0].EndedAtUtc='unexpected';},false);
run(f=>{delete f.rows[0].GateInstanceId;},false);
console.log('PASS crash snapshot: stable/change digest and 6 invalid cohorts');

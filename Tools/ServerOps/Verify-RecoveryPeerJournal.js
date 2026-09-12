// Same-account restart fixture: inspect only records outside the observed dead instances.
const prefix=process.env.ET_GAMEPLAY_ACCOUNT,excluded=JSON.parse(process.env.ET_RECOVERY_PEER_EXCLUDE||'null');
const expectedState=process.env.ET_RECOVERY_PEER_STATE;
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^e2e_[a-f0-9]{16}$/.test(prefix||'')||
   !Array.isArray(excluded)||excluded.length<1||excluded.length>100||excluded.some(x=>!/^[a-f0-9]{32}$/.test(x))||
   !['admitted','ended'].includes(expectedState))throw new Error('Invalid recovery peer context');
if(db.adminCommand({hello:1}).setName!=='et-local-rs')throw new Error('Only local replica set allowed');
const target=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const accounts=target.getCollection('ET.Server.Account').aggregate([
    {$match:{LoginName:{$in:Array.from({length:20},(_,i)=>prefix+'p'+i)}}},{$project:{_id:1}}
],{readConcern:{level:'majority'}}).toArray();
const rows=target.getCollection('ET.Server.AccountSessionJournal').aggregate([
    {$match:{AccountId:{$in:accounts.map(a=>a._id)},GateInstanceId:{$nin:excluded}}},{$sort:{_id:1}}
],{readConcern:{level:'majority'}}).toArray();
const passed=accounts.length===20&&rows.length===20&&new Set(rows.map(r=>String(r.AccountId))).size===20&&
    rows.every(r=>/^[a-f0-9]{32}$/.test(r.GateInstanceId||'')&&r.State===expectedState&&
        r.RecoveryOperationId===undefined&&r.InterruptedObservedAtUtc===undefined&&
        (expectedState==='admitted'?r.EndedAtUtc===undefined:r.EndedAtUtc instanceof Date));
print(JSON.stringify({Passed:passed,Accounts:accounts.length,Records:rows.length,ExpectedState:expectedState,
    GateInstanceIds:[...new Set(rows.map(r=>r.GateInstanceId))].sort(),
    SnapshotSha256:require('crypto').createHash('sha256').update(EJSON.stringify(rows)).digest('hex'),ReadOnly:true}));
if(!passed)quit(2);

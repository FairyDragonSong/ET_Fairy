// Local fault-fixture snapshot only. No recovery writes and no age-based inference.
const prefix=process.env.ET_GAMEPLAY_ACCOUNT, count=Number(process.env.ET_GAMEPLAY_CLIENTS);
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^e2e_[a-f0-9]{16}$/.test(prefix||'')||
   !Number.isInteger(count)||count<1||count>100)throw new Error('Invalid crash fixture');
if(db.adminCommand({hello:1}).setName!=='et-local-rs')throw new Error('Only local replica set allowed');
const target=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const names=Array.from({length:count},(_,i)=>prefix+'p'+i);
const accounts=target.getCollection('ET.Server.Account').aggregate([
    {$match:{LoginName:{$in:names}}},{$project:{_id:1}}
],{readConcern:{level:'majority'}}).toArray();
const rows=target.getCollection('ET.Server.AccountSessionJournal').aggregate([
    {$match:{AccountId:{$in:accounts.map(a=>a._id)}}},{$sort:{_id:1}}
],{readConcern:{level:'majority'}}).toArray();
const admitted=rows.filter(r=>r.State==='admitted'&&r.EndedAtUtc===undefined&&/^[a-f0-9]{32}$/.test(r.GateInstanceId||'')).length;
const uniqueAccounts=new Set(rows.map(r=>String(r.AccountId))).size;
const passed=accounts.length===count&&rows.length===count&&uniqueAccounts===count&&admitted===count;
print(JSON.stringify({Passed:passed,Accounts:accounts.length,Records:rows.length,Admitted:admitted,
    GateInstanceIds:[...new Set(rows.map(r=>r.GateInstanceId))].sort(),
    SnapshotSha256:require('crypto').createHash('sha256').update(EJSON.stringify(rows)).digest('hex'),ReadOnly:true}));
if(!passed)quit(2);

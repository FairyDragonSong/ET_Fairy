// Local fault-test recovery only. The caller supplies evidence from its own process handle.
// This is NOT a production authorization interface; environment values are not credentials.
const context=JSON.parse(process.env.ET_CRASH_RECOVERY_CONTEXT||'null');
const prefix=process.env.ET_GAMEPLAY_ACCOUNT, count=Number(process.env.ET_GAMEPLAY_CLIENTS);
const hex32=value=>typeof value==='string'&&/^[a-f0-9]{32}$/.test(value);
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^e2e_[a-f0-9]{16}$/.test(prefix||'')||
   !Number.isInteger(count)||count<1||count>100||!context||!context.ExitObserved||!hex32(context.OperationId)||
   !Number.isInteger(context.HostProcessId)||context.HostProcessId<=0||
   !/^[a-f0-9]{64}$/.test(context.BeforeSha256||'')||!Array.isArray(context.GateInstanceIds)||
   context.GateInstanceIds.length<1||context.GateInstanceIds.length>100||!context.GateInstanceIds.every(hex32))
    throw new Error('Invalid local recovery context');
const hello=db.adminCommand({hello:1});
if(hello.setName!=='et-local-rs'||!hello.hosts||!hello.hosts.every(h=>/^localhost:2710[123]$/.test(h)))
    throw new Error('Only the configured local test replica set is allowed');
const observed=new Date(context.ExitObservedAtUtc),started=new Date(context.ProcessStartedAtUtc);
if(!Number.isFinite(+observed)||!Number.isFinite(+started)||observed<started||observed>Date.now()+60000)
    throw new Error('Invalid process observation times');
const hash=value=>require('crypto').createHash('sha256').update(value).digest('hex');
const instances=[...new Set(context.GateInstanceIds)].sort();
const fingerprint=hash(JSON.stringify({prefix,count,instances,pid:context.HostProcessId,started:+started,observed:+observed,before:context.BeforeSha256}));
const session=db.getMongo().startSession();
let result,permissionDenied=false,stage='read';
try {
    session.withTransaction(()=>{
        stage='read';
        const target=session.getDatabase('ET_LOCAL_GAMEPLAY');
        const audit=target.getCollection('ET.Server.SessionJournalRecoveryAudit');
        const journal=target.getCollection('ET.Server.AccountSessionJournal');
        const oldAudit=audit.findOne({_id:context.OperationId});
        if(oldAudit&&oldAudit.Fingerprint!==fingerprint)throw new Error('Recovery operation id collision');
        const names=Array.from({length:count},(_,i)=>prefix+'p'+i);
        const accounts=target.getCollection('ET.Server.Account').find({LoginName:{$in:names}},{_id:1}).toArray();
        if(accounts.length!==count)throw new Error('Recovery account cohort mismatch');
        const filter={AccountId:{$in:accounts.map(a=>a._id)},GateInstanceId:{$in:instances}};
        const rows=journal.find(filter).sort({_id:1}).toArray();
        if(rows.length!==count||new Set(rows.map(r=>String(r.AccountId))).size!==count||
           rows.some(r=>!instances.includes(r.GateInstanceId)))throw new Error('Recovery instance cohort mismatch');
        if(oldAudit){
            if(oldAudit.Recovered!==count||rows.some(r=>r.State!=='interrupted'||r.RecoveryOperationId!==context.OperationId||
                +r.InterruptedObservedAtUtc!==+observed||r.EndedAtUtc!==undefined))throw new Error('Recovery replay evidence mismatch');
            if(hash(EJSON.stringify(rows))!==oldAudit.ResultSha256)throw new Error('Recovery result snapshot changed');
            result={Passed:true,Recovered:count,Replay:true};return;
        }
        if(rows.some(r=>r.State!=='admitted'||r.EndedAtUtc!==undefined)||hash(EJSON.stringify(rows))!==context.BeforeSha256)
            throw new Error('Recovery source snapshot changed');
        if(process.env.ET_CRASH_RECOVERY_CHECK_ROLLBACK==='1'){
            result={Passed:true,RolledBack:true,Records:count};return;
        }
        stage='update';
        const updated=journal.updateMany({...filter,GateInstanceId:{$in:instances},State:'admitted'},{$set:{
            State:'interrupted',InterruptedObservedAtUtc:observed,RecoveryOperationId:context.OperationId}});
        if(updated.modifiedCount!==count)throw new Error('Recovery update count mismatch');
        if(process.env.ET_CRASH_RECOVERY_TEST_FAIL==='1')throw new Error('Injected recovery audit failure');
        const resultSha256=hash(EJSON.stringify(journal.find(filter).sort({_id:1}).toArray()));
        stage='audit_insert';
        audit.insertOne({_id:context.OperationId,Fingerprint:fingerprint,GateInstanceIds:instances,
            HostProcessId:context.HostProcessId,ProcessStartedAtUtc:started,ExitObservedAtUtc:observed,
            SourceSha256:context.BeforeSha256,ResultSha256:resultSha256,Recovered:count,Reason:'local_owned_process_crash',RecordedAtUtc:new Date()});
        stage='commit';
        result={Passed:true,Recovered:count,Replay:false};
    },{readConcern:{level:'snapshot'},writeConcern:{w:'majority'}});
} catch(error){
    if(error.code===13)permissionDenied=true;else throw error;
} finally {session.endSession();}
if(permissionDenied){print(JSON.stringify({Passed:false,PermissionDenied:true,Code:13,Stage:stage}));quit(13);}
print(JSON.stringify(result));

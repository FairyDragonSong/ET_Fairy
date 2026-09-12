if (db.adminCommand({hello:1}).setName !== 'et-local-rs') throw new Error('Only the isolated local replica set is allowed');
const target = db.getSiblingDB('ET_LOCAL_ACCEPTANCE');
const probe = target.getCollection('ET.Server.PreproductionProbe');
const id = `failover-${new ObjectId()}`;
probe.insertOne({_id:id,value:1},{writeConcern:{w:'majority'}});
const before = db.adminCommand({hello:1}).primary;
const start = Date.now();
try {
    const directUri = process.env.ET_MONGO_URI.replace('localhost:27101,localhost:27102,localhost:27103',before).replace('replicaSet=et-local-rs','directConnection=true') + '&tlsCAFile=' + encodeURIComponent(process.env.ET_MONGO_CA_FILE);
    new Mongo(directUri).getDB('admin').runCommand({replSetStepDown:30,force:true});
} catch(e) { if(!/network|closed|connection|stepdown|stepped down/i.test(e.message)) throw e; }
let after;
for(let i=0;i<30;i++) {
    try { after=db.adminCommand({hello:1}).primary; if(after && after!==before) break; } catch {}
    sleep(1000);
}
if(!after || after===before) throw new Error('A different primary was not elected');
if(probe.findOne({_id:id})?.value !== 1) throw new Error('Majority committed record lost after failover');
probe.updateOne({_id:id},{$set:{value:2}},{writeConcern:{w:'majority'}});
if(probe.findOne({_id:id})?.value !== 2) throw new Error('Write after failover failed');
probe.deleteOne({_id:id},{writeConcern:{w:'majority'}});
print(JSON.stringify({test:'local-primary-stepdown',before,after,recoveryMilliseconds:Date.now()-start,majorityDataPreserved:true,writeAfterFailover:true}));

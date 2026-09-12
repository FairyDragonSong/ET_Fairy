const fs = require('fs');
const root = process.env.ET_LOCAL_MONGO_ROOT;
const credentials = JSON.parse(fs.readFileSync(`${root}/secrets.json`, 'utf8').replace(/^\uFEFF/, ''));
let connection;
for (let i=0; i<30; i++) {
    try { connection = new Mongo(`mongodb://localhost:27101/?directConnection=true&tls=true&serverSelectionTimeoutMS=2000&tlsCAFile=${encodeURIComponent(`${root}/ca.pem`)}`); break; }
    catch (error) { if (i===29) throw error; sleep(1000); }
}
const admin = connection.getDB('admin');
const result = admin.runCommand({replSetInitiate:{_id:'et-local-rs',members:[27101,27102,27103].map((port,i)=>({_id:i,host:`localhost:${port}`}))}});
if (!result.ok && result.codeName !== 'AlreadyInitialized') throw new Error('Replica set initiation failed');
for(let i=0;i<60;i++) {
    if(admin.runCommand({hello:1}).isWritablePrimary) break;
    if(i===59) throw new Error('Election timed out');
    sleep(1000);
}
admin.createUser({user:'etlocal',pwd:credentials.Password,roles:[{role:'root',db:'admin'}]});
print('Initialized authenticated three-node local replica set');

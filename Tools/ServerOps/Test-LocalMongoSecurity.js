if (db.adminCommand({hello:1}).setName !== 'et-local-rs') throw new Error('Only the isolated local replica set is allowed');
const ca = encodeURIComponent(process.env.ET_MONGO_CA_FILE);
let unauthenticatedRejected = false;
try {
    const anonymous = new Mongo(`mongodb://localhost:27101/?directConnection=true&tls=true&tlsCAFile=${ca}`);
    const response = anonymous.getDB('ET_LOCAL_ACCEPTANCE').runCommand({listCollections:1});
    unauthenticatedRejected = response.code === 13;
} catch(e) { if(e.code === 13) unauthenticatedRejected=true; else throw e; }
if (!unauthenticatedRejected) throw new Error('Anonymous collection access was accepted');

let wrongPasswordRejected=false;
try {
    new Mongo(`mongodb://etlocal:invalid-password@localhost:27101/?authSource=admin&directConnection=true&tls=true&tlsCAFile=${ca}&serverSelectionTimeoutMS=3000`);
} catch(e) { if(e.code===18 || /Authentication failed/i.test(e.message)) wrongPasswordRejected=true; else throw e; }
if(!wrongPasswordRejected) throw new Error('Invalid password was accepted');

let plaintextRejected=false;
try { new Mongo('mongodb://localhost:27101/?directConnection=true&tls=false&serverSelectionTimeoutMS=2000'); }
catch(e) { if(/connection|closed|socket|timed out|ECONNRESET/i.test(e.message)) plaintextRejected=true; else throw e; }
if(!plaintextRejected) throw new Error('Non-TLS connection accepted');

let untrustedCaRejected=false;
if (!process.env.ET_UNTRUSTED_CA_FILE) throw new Error('ET_UNTRUSTED_CA_FILE required for unrelated CA test');
try { new Mongo('mongodb://localhost:27101/?directConnection=true&tls=true&serverSelectionTimeoutMS=2000&tlsCAFile=' + encodeURIComponent(process.env.ET_UNTRUSTED_CA_FILE)); }
catch(e) { if(/certificate|self.signed|issuer|verify/i.test(e.message)) untrustedCaRejected=true; else throw e; }
if(!untrustedCaRejected) throw new Error('Connection without test CA unexpectedly accepted');

const session = db.getMongo().startSession();
let duplicateRejected=false;
try {
    session.startTransaction();
    session.getDatabase('ET_LOCAL_ACCEPTANCE').getCollection('ET.Server.Account').insertOne({_id:new ObjectId(),LoginName:'et_fixture_1'});
} catch(e) { if(e.code===11000) duplicateRejected=true; else throw e; }
finally {session.abortTransaction();session.endSession();}
if(!duplicateRejected) throw new Error('Unique LoginName constraint failed');
print(JSON.stringify({test:'local-security',unauthenticatedRejected,wrongPasswordRejected,plaintextRejected,untrustedCaRejected,duplicateRejected}));

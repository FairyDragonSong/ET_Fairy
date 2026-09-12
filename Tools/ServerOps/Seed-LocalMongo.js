// Explicitly scoped synthetic fixtures; never run against another database name.
const target = db.getSiblingDB('ET_LOCAL_ACCEPTANCE');
const accounts = target.getCollection('ET.Server.Account');
const roles = target.getCollection('ET.Server.Role');
if (!target.getCollection('ET.Server.DatabaseSchema').findOne({_id:'server-schema',Version:1})) throw new Error('Run ET Realm migration first');
if (accounts.countDocuments({}) || roles.countDocuments({})) throw new Error('Fixture database is not empty; refusing to overwrite');
const crypto = require('crypto');
const session = db.getMongo().startSession();
try {
    session.startTransaction({readConcern:{level:'snapshot'},writeConcern:{w:'majority'}});
    const targetSession=session.getDatabase('ET_LOCAL_ACCEPTANCE');
    for(let i=1;i<=20;i++) {
        const salt=crypto.randomBytes(16);
        const password=crypto.randomBytes(32);
        targetSession.getCollection('ET.Server.Account').insertOne({_id:Long(900000+i),LoginName:`et_fixture_${i}`,DisplayName:`fixture${i}`,Status:0,PasswordScheme:1,PasswordSalt:salt.toString('base64'),PasswordHash:crypto.pbkdf2Sync(password,salt,210000,32,'sha256').toString('base64'),PasswordIterations:210000,DefaultRoleId:Long(910000+i),CreateTime:Long(Date.now())});
        targetSession.getCollection('ET.Server.Role').insertOne({_id:Long(910000+i),AccountId:Long(900000+i),AccountName:`et_fixture_${i}`,Name:`fixture${i}`,MapName:'Map1',PositionX:i+0.25,PositionY:0,PositionZ:-i,LastSaveTime:Long(Date.now())});
    }
    session.commitTransaction();
} finally {session.endSession();}
print('Created 20 synthetic account/role pairs; random passwords discarded');

if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^e2e_[a-f0-9]{16}$/.test(process.env.ET_GAMEPLAY_ACCOUNT||'')||
   !/^[a-f0-9]{32}$/.test(process.env.ET_ADMIN_TEST_OPERATION||''))throw new Error('Only isolated export tests allowed');
const testDb=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const target=testDb.getCollection('ET.Server.Account').findOne({LoginName:process.env.ET_GAMEPLAY_ACCOUNT+'p0'});
if(!target)throw new Error('Test account missing');
const audit=testDb.getCollection('ET.Server.AccountDataRequestAudit').findOne({_id:process.env.ET_ADMIN_TEST_OPERATION});
print(JSON.stringify({AccountId:target._id.toString(),Version:(target.StateVersion||0).toString(),AuditCount:audit?1:0,
    Sha256:audit?audit.Sha256:null,Action:audit?audit.Action:null}));

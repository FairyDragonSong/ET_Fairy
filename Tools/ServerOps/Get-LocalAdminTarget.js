if (process.env.ET_MONGO_DATABASE !== 'ET_LOCAL_GAMEPLAY' || !/^e2e_[a-f0-9]{16}$/.test(process.env.ET_GAMEPLAY_ACCOUNT || '')) {
    throw new Error('Only isolated gameplay test accounts allowed');
}
const testDb = db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const target = testDb.getCollection('ET.Server.Account').findOne({LoginName: process.env.ET_GAMEPLAY_ACCOUNT + 'p0'});
if (!target) throw new Error('Test account missing');
print(JSON.stringify({AccountId: target._id.toString(), Status: target.Status,
    Version: (target.StateVersion || 0).toString(),
    ClosedAt: (target.ClosedAt || 0).toString(),
    AuditCount: testDb.getCollection('ET.Server.AccountStateAudit').countDocuments({AccountId: target._id}),
    RejectedAttempts: testDb.getCollection('ET.Server.AccountAdminAttempt').countDocuments({DeploymentId: process.env.ET_ADMIN_TEST_DEPLOYMENT || '',Outcome:'rejected'}),
    LimitedAttempts: testDb.getCollection('ET.Server.AccountAdminAttempt').countDocuments({DeploymentId: process.env.ET_ADMIN_TEST_DEPLOYMENT || '',Outcome:'rejected',Stage:'rate_limit'})}));

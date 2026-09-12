// Provision only per-test identities in the explicit local gameplay database.
const user=process.env.ET_ADMIN_TEST_USER;
const password=process.env.ET_ADMIN_TEST_PASSWORD;
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^et_admin_test_[a-f0-9]{32}$/.test(user||''))throw new Error('Invalid test identity');
if(db.adminCommand({hello:1}).setName!=='et-local-rs')throw new Error('Only local test replica set allowed');
const adminDb=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const privileges=(auditWrite)=>[
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.Account'},actions:['find','update']},
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.AccountStateAudit'},actions:['find','insert']},
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.AccountAdminAttempt'},actions:auditWrite?['find','insert']:['find']}
];
if(process.env.ET_ADMIN_TEST_CLEANUP==='1'){
    adminDb.dropUser(user);adminDb.dropRole(user);
    print('Local test administration identity removed; audits retained');
}else if(['deny','allow'].includes(process.env.ET_ADMIN_TEST_AUDIT_POLICY)){
    adminDb.updateRole(user,{privileges:privileges(process.env.ET_ADMIN_TEST_AUDIT_POLICY==='allow')});
    print('Local test attempt audit permission changed');
}else{
    if(!password||password.length<32)throw new Error('Test password missing');
    for(const name of ['ET.Server.AccountStateAudit','ET.Server.AccountAdminAttempt']){
        if(!adminDb.getCollectionNames().includes(name))adminDb.createCollection(name);
    }
    adminDb.createRole({role:user,privileges:privileges(true),roles:[]});
    adminDb.createUser({user,pwd:password,roles:[{role:user,db:'ET_LOCAL_GAMEPLAY'}]});
    print('Local least-privilege administration identity created');
}

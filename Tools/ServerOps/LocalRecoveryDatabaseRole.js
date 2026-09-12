const recoveryUser=process.env.ET_RECOVERY_TEST_USER;
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^et_recovery_test_[a-f0-9]{32}$/.test(recoveryUser||''))throw new Error('Invalid recovery test identity');
if(db.adminCommand({hello:1}).setName!=='et-local-rs')throw new Error('Only local replica set allowed');
const recoveryDb=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const recoveryPrivileges=[
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.Account'},actions:['find']},
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.AccountSessionJournal'},actions:['find','update']},
    {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.SessionJournalRecoveryAudit'},actions:process.env.ET_RECOVERY_ROLE_DENY_AUDIT==='1'?['find']:['find','insert']}
];
if(process.env.ET_RECOVERY_ROLE_CLEANUP==='1'){
    if(recoveryDb.getUser(recoveryUser))recoveryDb.dropUser(recoveryUser);
    if(recoveryDb.getRole(recoveryUser))recoveryDb.dropRole(recoveryUser);
    if(recoveryDb.getUser(recoveryUser)||recoveryDb.getRole(recoveryUser))throw new Error('Recovery identity cleanup not verified');
    print('Temporary recovery identity removed; journal and audit retained');
}else if(process.env.ET_RECOVERY_ROLE_UPDATE==='1'){
    if(!recoveryDb.getUser(recoveryUser)||!recoveryDb.getRole(recoveryUser))throw new Error('Existing recovery test identity required');
    recoveryDb.updateRole(recoveryUser,{roles:[],privileges:recoveryPrivileges});
    print('Temporary recovery role updated');
}else{
    if(!/^[a-f0-9]{64}$/i.test(process.env.ET_RECOVERY_TEST_PASSWORD||''))throw new Error('Recovery test password missing');
    if(!recoveryDb.getCollectionNames().includes('ET.Server.SessionJournalRecoveryAudit'))recoveryDb.createCollection('ET.Server.SessionJournalRecoveryAudit');
    recoveryDb.createRole({role:recoveryUser,roles:[],privileges:recoveryPrivileges});
    recoveryDb.createUser({user:recoveryUser,pwd:process.env.ET_RECOVERY_TEST_PASSWORD,roles:[{role:recoveryUser,db:'ET_LOCAL_GAMEPLAY'}]});
    print('Temporary least-privilege recovery identity created');
}

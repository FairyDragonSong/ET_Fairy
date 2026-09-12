// Only an ephemeral identity on the explicitly isolated local replica set.
const user=process.env.ET_SELF_TEST_USER;
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^et_self_test_[a-f0-9]{32}$/.test(user||''))throw new Error('Invalid test identity');
if(db.adminCommand({hello:1}).setName!=='et-local-rs')throw new Error('Only local test replica set allowed');
const target=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
if(process.env.ET_SELF_TEST_CLEANUP==='1'){
    target.dropUser(user);
    target.dropRole(user);
    if(target.getUser(user)!==null||target.getRole(user)!==null)throw new Error('Temporary identity cleanup not verified');
    print('Temporary self-password test user and role removed; account evidence retained');
}else{
    const password=process.env.ET_SELF_TEST_PASSWORD;
    if(!password||password.length<32)throw new Error('Test password missing');
    target.createRole({role:user,roles:[],privileges:[
        {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.Account'},actions:['find','update']},
        {resource:{db:'ET_LOCAL_GAMEPLAY',collection:'ET.Server.AccountStateAudit'},actions:['find']}
    ]});
    target.createUser({user,pwd:password,roles:[{role:user,db:'ET_LOCAL_GAMEPLAY'}]});
    print('Temporary account-update-only identity created; audit insert denied');
}

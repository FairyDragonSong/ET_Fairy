// Connect-Mongo.js must be loaded using the limited administrator credential.
const limited=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const commands=[
    {update:'ET.Server.AccountStateAudit',updates:[{q:{_id:'nonexistent-permission-probe'},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.AccountStateAudit',deletes:[{q:{_id:'nonexistent-permission-probe'},limit:1}]},
    {delete:'ET.Server.AccountAdminAttempt',deletes:[{q:{_id:'nonexistent-permission-probe'},limit:1}]},
    {update:'ET.Server.AccountAdminAttempt',updates:[{q:{_id:'nonexistent-permission-probe'},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.Account',deletes:[{q:{_id:'nonexistent-permission-probe'},limit:1}]},
    {find:'ET.Server.Role',filter:{_id:'nonexistent-permission-probe'}}
];
for(const command of commands){
    let rejected=false;
    try{const result=limited.runCommand(command);rejected=result.code===13;}
    catch(error){if(error.code===13)rejected=true;else throw error;}
    if(!rejected)throw new Error('Forbidden database operation was not denied');
}
print('PASS audit update/delete, account delete and role access denied');

const limited=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
for(const name of ['ET.Server.Account','ET.Server.Role','ET.Server.AccountSessionJournal','ET.Server.AccountStateAudit','ET.Server.AccountDataRequestAudit']){
    const result=limited.runCommand({find:name,filter:{_id:'permission-probe'},limit:1});
    if(!result.ok)throw new Error('Required export read denied: '+name);
}
const forbidden=[
    {update:'ET.Server.Account',updates:[{q:{_id:'permission-probe'},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.Account',deletes:[{q:{_id:'permission-probe'},limit:1}]},
    {update:'ET.Server.Role',updates:[{q:{_id:'permission-probe'},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.AccountDataRequestAudit',deletes:[{q:{_id:'permission-probe'},limit:1}]},
    {find:'ET.Server.DatabaseSchema',filter:{_id:'permission-probe'}}
];
for(const command of forbidden){
    let rejected=false;
    try{const result=limited.runCommand(command);rejected=result.code===13;}
    catch(error){if(error.code===13)rejected=true;else throw error;}
    if(!rejected)throw new Error('Forbidden export operation was not denied');
}
print('PASS export identity reads only approved collections and cannot mutate account data');

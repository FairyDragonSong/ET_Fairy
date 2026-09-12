const recoveryTarget=db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const recoveryProbe='nonexistent-permission-probe';
const deniedCommands=[
    {update:'ET.Server.Account',updates:[{q:{_id:recoveryProbe},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.Account',deletes:[{q:{_id:recoveryProbe},limit:1}]},
    {delete:'ET.Server.AccountSessionJournal',deletes:[{q:{_id:recoveryProbe},limit:1}]},
    {update:'ET.Server.SessionJournalRecoveryAudit',updates:[{q:{_id:recoveryProbe},u:{$set:{probe:true}}}]},
    {delete:'ET.Server.SessionJournalRecoveryAudit',deletes:[{q:{_id:recoveryProbe},limit:1}]},
    {find:'ET.Server.Role',filter:{_id:recoveryProbe}}
];
for(const command of deniedCommands){
    let rejected=false;
    try{const response=recoveryTarget.runCommand(command);rejected=response.code===13;}
    catch(error){if(error.code===13)rejected=true;else throw error;}
    if(!rejected)throw new Error('Forbidden recovery database operation was not rejected');
}
print(JSON.stringify({Passed:true,DeniedOperations:deniedCommands.length,ExpectedCode:13}));

// Read-only, cohort-specific post-shutdown check. Never repairs historical records.
const cohort = process.env.ET_GAMEPLAY_ACCOUNT;
const expected = Number(process.env.ET_GAMEPLAY_CLIENTS);
if (process.env.ET_MONGO_DATABASE !== 'ET_LOCAL_GAMEPLAY' ||
    !/^e2e_[a-f0-9]{16}$/.test(cohort || '') || !Number.isInteger(expected) || expected < 1 || expected > 3000)
    throw new Error('Invalid isolated gameplay cohort');
if (db.adminCommand({hello:1}).setName !== 'et-local-rs') throw new Error('Only local replica set allowed');
const targetDb = db.getSiblingDB('ET_LOCAL_GAMEPLAY');
const names = Array.from({length:expected}, (_,i) => cohort + 'p' + i);
const accountRows = targetDb.getCollection('ET.Server.Account').aggregate([
    {$match:{LoginName:{$in:names}}}, {$project:{_id:1,LoginName:1,StateVersion:1}}
], {readConcern:{level:'majority'}}).toArray();
const accountsById = new Map(accountRows.map(row => [String(row._id), row]));
const journalRows = targetDb.getCollection('ET.Server.AccountSessionJournal').aggregate([
    {$match:{AccountId:{$in:accountRows.map(row=>row._id)}}},
    {$project:{AccountId:1,AccountStateVersion:1,State:1,OpenedAtUtc:1,EndedAtUtc:1,DisconnectError:1}}
], {readConcern:{level:'majority'}}).toArray();
const perAccount = new Map();
let unfinished = 0, invalid = 0;
for (const row of journalRows) {
    const key = String(row.AccountId);
    const account = accountsById.get(key);
    perAccount.set(key, (perAccount.get(key)||0)+1);
    if (row.State !== 'ended') unfinished++;
    if (!(row.OpenedAtUtc instanceof Date) || !(row.EndedAtUtc instanceof Date) ||
        row.EndedAtUtc < row.OpenedAtUtc || row.DisconnectError === undefined ||
        row.AccountStateVersion === undefined || !account || account.StateVersion === undefined ||
        String(row.AccountStateVersion) !== String(account.StateVersion)) invalid++;
}
const missing = accountRows.filter(row=>!perAccount.has(String(row._id))).length;
const duplicates = [...perAccount.values()].filter(count=>count!==1).length;
const passed = accountRows.length===expected && new Set(accountRows.map(row=>row.LoginName)).size===expected &&
    journalRows.length===expected && missing===0 && duplicates===0 && unfinished===0 && invalid===0;
print(JSON.stringify({Passed:passed,Expected:expected,Accounts:accountRows.length,Records:journalRows.length,
    Missing:missing,Duplicates:duplicates,Unfinished:unfinished,Invalid:invalid,ReadOnly:true}));
if (!passed) quit(2);

const sourceDatabaseName = process.env.ET_RESTORE_SOURCE_DATABASE;
const restoredDatabaseName = process.env.ET_RESTORE_TARGET_DATABASE;
if (!sourceDatabaseName || !restoredDatabaseName) {
    throw new Error("ET_RESTORE_SOURCE_DATABASE and ET_RESTORE_TARGET_DATABASE are required");
}
if (sourceDatabaseName === restoredDatabaseName) {
    throw new Error("restore verification target must differ from source");
}

const source = db.getSiblingDB(sourceDatabaseName);
const restored = db.getSiblingDB(restoredDatabaseName);
const collections = ["ET.Server.Account", "ET.Server.Role", "ET.Server.DatabaseSchema"];
const counts = {};
const crypto = require('crypto');
function canonical(value) {
    if (Array.isArray(value)) return value.map(canonical);
    if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(k=>[k,canonical(value[k])]));
    return value;
}
function digest(collection) {
    const hash = crypto.createHash('sha256');
    for (const document of collection.find({}).sort({_id:1})) {
        hash.update(JSON.stringify(canonical(EJSON.serialize(document,{relaxed:false}))) + '\n');
    }
    return hash.digest('hex');
}
for (const collectionName of collections) {
    const sourceCount = source.getCollection(collectionName).countDocuments({});
    const restoredCount = restored.getCollection(collectionName).countDocuments({});
    counts[collectionName] = { source: sourceCount, restored: restoredCount };
    if (sourceCount !== restoredCount) {
        throw new Error(`document count mismatch for ${collectionName}: source=${sourceCount}, restored=${restoredCount}`);
    }
    const sourceHash = digest(source.getCollection(collectionName));
    const restoredHash = digest(restored.getCollection(collectionName));
    if (sourceHash !== restoredHash) throw new Error(`document content mismatch for ${collectionName}`);
    const sourceIndexes = source.getCollection(collectionName).getIndexes().map(canonical).sort((a,b)=>a.name.localeCompare(b.name));
    const restoredIndexes = restored.getCollection(collectionName).getIndexes().map(canonical).sort((a,b)=>a.name.localeCompare(b.name));
    if (JSON.stringify(sourceIndexes) !== JSON.stringify(restoredIndexes)) throw new Error(`index mismatch for ${collectionName}`);
    counts[collectionName].contentHash = sourceHash;
    counts[collectionName].indexesVerified = true;
}

const restoredAccounts = restored.getCollection("ET.Server.Account");
const restoredRoles = restored.getCollection("ET.Server.Role");
const duplicateLoginNames = restoredAccounts.aggregate([
    { $group: { _id: "$LoginName", count: { $sum: 1 } } },
    { $match: { count: { $gt: 1 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;
const duplicateRoleOwners = restoredRoles.aggregate([
    { $group: { _id: "$AccountId", count: { $sum: 1 } } },
    { $match: { count: { $gt: 1 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;
const accountsMissingRoles = restoredAccounts.aggregate([
    { $match: { DefaultRoleId: { $ne: NumberLong(0) } } },
    { $lookup: { from: "ET.Server.Role", localField: "DefaultRoleId", foreignField: "_id", as: "role" } },
    { $match: { role: { $size: 0 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;
const orphanRoles = restoredRoles.aggregate([
    { $lookup: { from: "ET.Server.Account", localField: "AccountId", foreignField: "_id", as: "account" } },
    { $match: { account: { $size: 0 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;

if (duplicateLoginNames || duplicateRoleOwners || accountsMissingRoles || orphanRoles) {
    throw new Error("restored account-role consistency audit failed");
}

print(JSON.stringify({
    sourceDatabaseName,
    restoredDatabaseName,
    counts,
    duplicateLoginNames,
    duplicateRoleOwners,
    accountsMissingRoles,
    orphanRoles
}));

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
for (const collectionName of collections) {
    const sourceCount = source.getCollection(collectionName).countDocuments({});
    const restoredCount = restored.getCollection(collectionName).countDocuments({});
    counts[collectionName] = { source: sourceCount, restored: restoredCount };
    if (sourceCount !== restoredCount) {
        throw new Error(`document count mismatch for ${collectionName}: source=${sourceCount}, restored=${restoredCount}`);
    }
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

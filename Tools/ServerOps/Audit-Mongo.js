const databaseName = process.env.ET_AUDIT_DATABASE;
if (!databaseName) {
    throw new Error("ET_AUDIT_DATABASE is required");
}

const target = db.getSiblingDB(databaseName);
const accounts = target.getCollection("ET.Server.Account");
const roles = target.getCollection("ET.Server.Role");

const duplicateLoginNames = accounts.aggregate([
    { $group: { _id: "$LoginName", count: { $sum: 1 } } },
    { $match: { count: { $gt: 1 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;

const duplicateRoleOwners = roles.aggregate([
    { $group: { _id: "$AccountId", count: { $sum: 1 } } },
    { $match: { count: { $gt: 1 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;

const accountsMissingRoles = accounts.aggregate([
    { $match: { DefaultRoleId: { $ne: NumberLong(0) } } },
    { $lookup: { from: "ET.Server.Role", localField: "DefaultRoleId", foreignField: "_id", as: "role" } },
    { $match: { role: { $size: 0 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;

const orphanRoles = roles.aggregate([
    { $lookup: { from: "ET.Server.Account", localField: "AccountId", foreignField: "_id", as: "account" } },
    { $match: { account: { $size: 0 } } },
    { $count: "count" }
]).toArray()[0]?.count ?? 0;

const result = { databaseName, duplicateLoginNames, duplicateRoleOwners, accountsMissingRoles, orphanRoles };
print(JSON.stringify(result));
if (duplicateLoginNames || duplicateRoleOwners || accountsMissingRoles || orphanRoles) {
    quit(2);
}

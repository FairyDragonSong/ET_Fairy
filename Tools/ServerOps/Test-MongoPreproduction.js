const databaseName = process.env.ET_ACCEPTANCE_DATABASE;
if (!databaseName) {
    throw new Error("ET_ACCEPTANCE_DATABASE is required");
}

const admin = db.getSiblingDB("admin");
const hello = admin.runCommand({ hello: 1 });
const connectionStatus = admin.runCommand({ connectionStatus: 1, showPrivileges: false });
const authenticatedUsers = connectionStatus.authInfo?.authenticatedUsers ?? [];
const topology = hello.msg === "isdbgrid" ? "sharded" : (hello.setName ? "replicaSet" : "standalone");
if (topology === "standalone") {
    throw new Error("MongoDB must be a replica set or sharded cluster");
}
if (authenticatedUsers.length === 0) {
    throw new Error("MongoDB connection is not authenticated");
}
if (!hello.logicalSessionTimeoutMinutes) {
    throw new Error("MongoDB deployment does not advertise session support");
}

const target = db.getSiblingDB(databaseName);
const expectedIndexes = [
    ["ET.Server.Account", "ux_account_login_name", true, {LoginName:1}],
    ["ET.Server.Account", "ix_account_default_role", false, {DefaultRoleId:1}],
    ["ET.Server.Role", "ux_role_account_id", true, {AccountId:1}],
    ["ET.Server.Role", "ix_role_last_save_time", false, {LastSaveTime:1}]
];
const missingIndexes = [];
for (const [collectionName, indexName, unique, key] of expectedIndexes) {
    const index = target.getCollection(collectionName).getIndexes().find(candidate => candidate.name === indexName);
    if (!index || (unique && index.unique !== true) || JSON.stringify(index.key) !== JSON.stringify(key) || (indexName === 'ix_account_default_role' && !index.sparse)) {
        missingIndexes.push({ collectionName, indexName, uniqueRequired: unique });
    }
}
if (missingIndexes.length > 0) {
    throw new Error(`required indexes are missing or invalid: ${JSON.stringify(missingIndexes)}`);
}

const probeCollection = target.getCollection("ET.Server.PreproductionProbe");
if (!target.getCollectionNames().includes('ET.Server.PreproductionProbe')) target.createCollection('ET.Server.PreproductionProbe');
const marker = `acceptance-${new ObjectId().toString()}`;
probeCollection.deleteMany({ _id: marker });

const session = db.getMongo().startSession();
try {
    const sessionDatabase = session.getDatabase(databaseName);
    const sessionProbe = sessionDatabase.getCollection("ET.Server.PreproductionProbe");

    session.startTransaction({ readConcern: { level: "snapshot" }, writeConcern: { w: "majority" } });
    sessionProbe.insertOne({ _id: marker, state: "aborted", createdAt: new Date() });
    session.abortTransaction();
    if (probeCollection.countDocuments({ _id: marker }) !== 0) {
        throw new Error("aborted transaction left persisted data");
    }

    session.startTransaction({ readConcern: { level: "snapshot" }, writeConcern: { w: "majority" } });
    sessionProbe.insertOne({ _id: marker, state: "committed", createdAt: new Date() });
    session.commitTransaction();
    if (probeCollection.countDocuments({ _id: marker, state: "committed" }) !== 1) {
        throw new Error("committed transaction was not visible");
    }
}
finally {
    session.endSession();
    probeCollection.deleteMany({ _id: marker }, { writeConcern: { w: "majority" } });
}

print(JSON.stringify({
    databaseName,
    topology,
    replicaSetName: hello.setName ?? null,
    writablePrimary: hello.isWritablePrimary === true,
    logicalSessionTimeoutMinutes: hello.logicalSessionTimeoutMinutes,
    authenticatedUserCount: authenticatedUsers.length,
    requiredIndexCount: expectedIndexes.length,
    transactionAbortVerified: true,
    transactionCommitVerified: true
}));

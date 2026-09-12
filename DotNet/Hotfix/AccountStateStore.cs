using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

// Persistence primitive only. A trusted administration service must authenticate and
// authorize the operator before calling it. No client or console endpoint is registered.
[FriendOf(typeof(DBComponent))]
internal static class AccountStateStore
{
    internal const string AuditCollection = "ET.Server.AccountStateAudit";

    internal static async ETTask<bool> Change(DBComponent database, long accountId, long expectedVersion,
        AccountStatus target, string operatorId, string reasonCode, string operationId, Action beforeAudit = null)
    {
        if (accountId <= 0 || expectedVersion < 0 || expectedVersion == long.MaxValue ||
            (target != AccountStatus.Active && target != AccountStatus.Disabled) ||
            !ValidLabel(operatorId) || !ValidLabel(reasonCode) ||
            !Guid.TryParseExact(operationId, "N", out _))
            throw new InvalidOperationException("Invalid account state command");

        var accounts = database.database.GetCollection<Account>(typeof(Account).FullName);
        var audits = database.database.GetCollection<BsonDocument>(AuditCollection);
        using var session = await database.mongoClient.StartSessionAsync();
        return await session.WithTransactionAsync(async (transaction, cancellation) =>
        {
            var prior = await audits.Find(transaction, Builders<BsonDocument>.Filter.Eq("_id", operationId))
                .FirstOrDefaultAsync(cancellation);
            if (prior != null)
            {
                if (prior["AccountId"].AsInt64 != accountId || prior["ExpectedVersion"].AsInt64 != expectedVersion ||
                    prior["Target"].AsInt32 != (int)target || prior["OperatorId"].AsString != operatorId ||
                    prior["ReasonCode"].AsString != reasonCode)
                    throw new InvalidOperationException("Operation id belongs to another command");
                return false;
            }

            var account = await accounts.Find(transaction, a => a.Id == accountId).SingleOrDefaultAsync(cancellation);
            if (account == null || account.StateVersion != expectedVersion || account.Status == target)
                throw new InvalidOperationException("Account state conflict");
            var changed = await accounts.UpdateOneAsync(transaction,
                a => a.Id == accountId && a.Status == account.Status,
                Builders<Account>.Update.Set(a => a.Status, target).Set(a => a.StateVersion, expectedVersion + 1),
                cancellationToken: cancellation);
            if (changed.MatchedCount != 1) throw new InvalidOperationException("Account state conflict");
            beforeAudit?.Invoke();
            await audits.InsertOneAsync(transaction, new BsonDocument
            {
                { "_id", operationId }, { "AccountId", accountId }, { "ExpectedVersion", expectedVersion },
                { "Version", expectedVersion + 1 }, { "Previous", (int)account.Status }, { "Target", (int)target },
                { "OperatorId", operatorId }, { "ReasonCode", reasonCode }, { "CreatedAtUtc", DateTime.UtcNow }
            }, cancellationToken: cancellation);
            return true;
        }, new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
    }

    private static bool ValidLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        foreach (char c in value)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) return false;
        return true;
    }
}

using System;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server
{
    [FriendOf(typeof(DBComponent))]
    [FriendOf(typeof(SharedLoginAdmissionComponent))]
    public static class SharedLoginAdmissionStore
    {
        internal const string CollectionName = "ET.Server.LoginAdmissionWindow";

        public static async ETTask EnsureIndexes(DBComponent database)
        {
            IMongoCollection<BsonDocument> collection = database.database.GetCollection<BsonDocument>(CollectionName);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ExpiresAtUtc"),
                new CreateIndexOptions { Name = "ttl_login_admission", ExpireAfter = TimeSpan.Zero }));
        }

        public static async ETTask<SharedLoginAdmissionResult> TryAcquire(
            Scene root,
            SharedLoginAdmissionComponent component,
            string ipAddress,
            string accountInput)
        {
            DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
            return await TryAcquire(database, root.Zone(), component, ipAddress, accountInput);
        }

        public static async ETTask<SharedLoginAdmissionResult> TryAcquire(
            DBComponent database,
            int zone,
            SharedLoginAdmissionComponent component,
            string ipAddress,
            string accountInput)
        {
            if (!component.Enabled)
            {
                return SharedLoginAdmissionResult.Accepted();
            }

            try
            {
                long nowMilliseconds = TimeInfo.Instance.ServerNow();
                long windowMilliseconds = Math.Max(1, Options.Instance.LoginRateWindowSeconds) * 1000L;
                long windowStart = nowMilliseconds - nowMilliseconds % windowMilliseconds;
                IMongoCollection<BsonDocument> collection = database.database.GetCollection<BsonDocument>(CollectionName)
                    .WithWriteConcern(WriteConcern.WMajority);

                bool ipAccepted = await Consume(collection, zone, "ip", ipAddress ?? "unknown",
                    Math.Max(1, Options.Instance.LoginIpLimit), windowStart, windowMilliseconds, component.HmacKey);
                if (!ipAccepted)
                {
                    return SharedLoginAdmissionResult.Rejected(RetryAfter(nowMilliseconds, windowStart, windowMilliseconds));
                }

                if (!AccountInputValidator.TryNormalizeLoginName(accountInput, out string loginName))
                {
                    return SharedLoginAdmissionResult.Accepted();
                }

                bool accountAccepted = await Consume(collection, zone, "account", loginName,
                    Math.Max(1, Options.Instance.LoginAccountLimit), windowStart, windowMilliseconds, component.HmacKey);
                return accountAccepted
                    ? SharedLoginAdmissionResult.Accepted()
                    : SharedLoginAdmissionResult.Rejected(RetryAfter(nowMilliseconds, windowStart, windowMilliseconds));
            }
            catch (Exception error)
            {
                Log.Error($"shared login admission storage error: {error.GetType().Name}");
                return SharedLoginAdmissionResult.Unavailable();
            }
        }

        public static string CreateDocumentId(int zone, string kind, string value, long windowStart, byte[] hmacKey)
        {
            if (zone <= 0 || kind is not ("ip" or "account") || string.IsNullOrEmpty(value) || hmacKey?.Length != 32)
            {
                throw new InvalidOperationException("Shared login admission identity is invalid");
            }
            byte[] input = Encoding.UTF8.GetBytes(kind + "\0" + value);
            string digest = Convert.ToHexString(HMACSHA256.HashData(hmacKey, input)).ToLowerInvariant();
            return $"{zone}:{windowStart}:{kind}:{digest}";
        }

        private static async ETTask<bool> Consume(IMongoCollection<BsonDocument> collection, int zone, string kind,
            string value, int limit, long windowStart, long windowMilliseconds, byte[] hmacKey)
        {
            string id = CreateDocumentId(zone, kind, value, windowStart, hmacKey);
            FilterDefinition<BsonDocument> filter = Builders<BsonDocument>.Filter.Eq("_id", id) &
                    (Builders<BsonDocument>.Filter.Lt("Count", limit) | Builders<BsonDocument>.Filter.Exists("Count", false));
            UpdateDefinition<BsonDocument> update = Builders<BsonDocument>.Update
                .Inc("Count", 1)
                .SetOnInsert("Zone", zone)
                .SetOnInsert("Kind", kind)
                .SetOnInsert("WindowStartUtc", DateTimeOffset.FromUnixTimeMilliseconds(windowStart).UtcDateTime)
                .SetOnInsert("ExpiresAtUtc", DateTimeOffset.FromUnixTimeMilliseconds(windowStart + windowMilliseconds * 2).UtcDateTime);

            for (int attempt = 0; attempt < 2; ++attempt)
            {
                try
                {
                    UpdateResult result = await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });
                    return result.MatchedCount == 1 || result.UpsertedId != null;
                }
                catch (MongoWriteException error) when (error.WriteError?.Code == 11000)
                {
                    if (attempt == 1) return false;
                }
            }
            return false;
        }

        private static long RetryAfter(long now, long start, long window) => Math.Max(1, start + window - now);
    }

    public readonly struct SharedLoginAdmissionResult
    {
        public bool IsAccepted { get; }

        public bool IsAvailable { get; }

        public long RetryAfterMilliseconds { get; }

        private SharedLoginAdmissionResult(bool accepted, bool available, long retryAfterMilliseconds)
        {
            this.IsAccepted = accepted;
            this.IsAvailable = available;
            this.RetryAfterMilliseconds = retryAfterMilliseconds;
        }

        public static SharedLoginAdmissionResult Accepted() => new(true, true, 0);

        public static SharedLoginAdmissionResult Unavailable() => new(false, false, 0);

        public static SharedLoginAdmissionResult Rejected(long retryAfterMilliseconds) => new(false, true, retryAfterMilliseconds);
    }
}

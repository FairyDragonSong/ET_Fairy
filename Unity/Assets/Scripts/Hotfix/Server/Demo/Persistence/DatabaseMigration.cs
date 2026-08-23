using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server
{
    [FriendOf(typeof(DBComponent))]
    public static class DatabaseMigration
    {
        private const int CurrentVersion = 1;
        private const string MigrationCollection = "ET.Server.DatabaseSchema";
        private const string SchemaId = "server-schema";

        public static async ETTask Apply(Scene root)
        {
            DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
            IMongoCollection<BsonDocument> migrations = database.database.GetCollection<BsonDocument>(MigrationCollection);
            BsonDocument schema = await migrations.Find(Builders<BsonDocument>.Filter.Eq("_id", SchemaId)).FirstOrDefaultAsync();
            int version = schema?.GetValue("Version", 0).AsInt32 ?? 0;
            if (version > CurrentVersion)
            {
                throw new InvalidOperationException($"database schema {version} is newer than server schema {CurrentVersion}");
            }

            if (version < 1)
            {
                await ApplyVersion1(database);
                await SaveVersion(migrations, 1);
                version = 1;
            }

            Log.Info($"database schema ready: database={database.database.DatabaseNamespace.DatabaseName}, version={version}");
        }

        private static async ETTask ApplyVersion1(DBComponent database)
        {
            IMongoCollection<Account> accounts = database.database.GetCollection<Account>(typeof(Account).FullName);
            await accounts.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<Account>(
                    Builders<Account>.IndexKeys.Ascending(account => account.LoginName),
                    new CreateIndexOptions { Name = "ux_account_login_name", Unique = true }),
                new CreateIndexModel<Account>(
                    Builders<Account>.IndexKeys.Ascending(account => account.DefaultRoleId),
                    new CreateIndexOptions<Account> { Name = "ix_account_default_role", Sparse = true }),
            });

            IMongoCollection<Role> roles = database.database.GetCollection<Role>(typeof(Role).FullName);
            await roles.Indexes.CreateManyAsync(new[]
            {
                new CreateIndexModel<Role>(
                    Builders<Role>.IndexKeys.Ascending(role => role.AccountId),
                    new CreateIndexOptions { Name = "ux_role_account_id", Unique = true }),
                new CreateIndexModel<Role>(
                    Builders<Role>.IndexKeys.Ascending(role => role.LastSaveTime),
                    new CreateIndexOptions { Name = "ix_role_last_save_time" }),
            });
        }

        private static async ETTask SaveVersion(IMongoCollection<BsonDocument> migrations, int version)
        {
            BsonDocument document = new()
            {
                { "_id", SchemaId },
                { "Version", version },
                { "AppliedAt", DateTime.UtcNow },
            };
            await migrations.ReplaceOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", SchemaId),
                document,
                new ReplaceOptions { IsUpsert = true });
        }
    }
}

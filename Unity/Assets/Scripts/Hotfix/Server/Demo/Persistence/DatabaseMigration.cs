using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server
{
    [FriendOf(typeof(DBComponent))]
    public static class DatabaseMigration
    {
        public const int MinimumReadableVersion = 0;
        public const int MaximumReadableVersion = 1;
        public const int TargetVersion = 1;
        private const string MigrationCollection = "ET.Server.DatabaseSchema";
        private const string SchemaId = "server-schema";

        // 按版本顺序应用数据库迁移。
        public static async ETTask Apply(Scene root)
        {
            DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
            IMongoCollection<BsonDocument> migrations = database.database.GetCollection<BsonDocument>(MigrationCollection);
            BsonDocument schema = await migrations.Find(Builders<BsonDocument>.Filter.Eq("_id", SchemaId)).FirstOrDefaultAsync();
            int version = schema?.GetValue("Version", 0).AsInt32 ?? 0;
            if (version < MinimumReadableVersion || version > MaximumReadableVersion)
            {
                throw new InvalidOperationException(
                    $"database schema {version} is outside readable range {MinimumReadableVersion}-{MaximumReadableVersion}");
            }

            // 版本 1：建立账号与角色持久化所需的基础索引和唯一约束。
            // - Account.LoginName 唯一，防止并发注册产生重复账号；
            // - Account.DefaultRoleId 使用稀疏索引，支持账号与默认角色的关联查询；
            // - Role.AccountId 唯一，保证当前阶段一个账号只有一个角色；
            // - Role.LastSaveTime 建立索引，支持角色保存审计与恢复检查。
            if (version < 1)
            {
                await ApplyVersion1(database);
                await SaveVersion(migrations, 1);
                version = 1;
            }

            if (version != TargetVersion)
            {
                throw new InvalidOperationException($"database migration target {TargetVersion} was not reached: {version}");
            }

            await SharedLoginAdmissionStore.EnsureIndexes(database);

            Log.Info($"database schema ready: database={database.database.DatabaseNamespace.DatabaseName}, version={version}, " +
                     $"readable_min={MinimumReadableVersion}, readable_max={MaximumReadableVersion}");
        }

        // 应用版本 1 的数据库结构调整：创建账号和角色持久化所需的索引与唯一约束。
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

        // 持久化已完成的数据库迁移版本。
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

using System;
using System.Collections.Generic;
using MongoDB.Driver;
using Unity.Mathematics;

namespace ET.Server
{
    public readonly struct RoleSnapshot
    {
        public long Id { get; }
        public string Name { get; }
        public string MapName { get; }
        public float3 Position { get; }

        public RoleSnapshot(long id, string name, string mapName, float3 position)
        {
            this.Id = id;
            this.Name = name;
            this.MapName = mapName;
            this.Position = position;
        }
    }

    public readonly struct RoleLoadResult
    {
        public bool IsSuccess { get; }
        public int Error { get; }
        public string Message { get; }
        public RoleSnapshot Role { get; }

        private RoleLoadResult(bool isSuccess, int error, string message, RoleSnapshot role)
        {
            this.IsSuccess = isSuccess;
            this.Error = error;
            this.Message = message;
            this.Role = role;
        }

        public static RoleLoadResult Success(RoleSnapshot role) => new(true, 0, string.Empty, role);

        public static RoleLoadResult Failure(string message) =>
            new(false, ErrorCore.ERR_RoleStorageUnavailable, message, default);
    }

    [FriendOf(typeof(DBComponent))]
    public static class RoleRepository
    {
        public static async ETTask<RoleLoadResult> LoadOrCreate(Scene root, string loginName)
        {
            long accountLockKey = loginName.GetLongHashCode();
            using (await root.GetComponent<CoroutineLockComponent>().Wait(CoroutineLockType.Account, accountLockKey))
            {
                try
                {
                    DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
                    List<Account> accounts = await database.Query<Account>(accountLockKey, account => account.LoginName == loginName);
                    if (accounts.Count != 1)
                    {
                        Log.Error($"role load account count invalid: accountKey: {accountLockKey} count: {accounts.Count}");
                        return RoleLoadResult.Failure("角色服务暂时不可用");
                    }

                    Account account = accounts[0];
                    if (account.Status != AccountStatus.Active) return RoleLoadResult.Failure("账号当前不可用");
                    if (account.DefaultRoleId == 0)
                    {
                        return await CreateDefaultRole(root, database, accountLockKey, account);
                    }

                    Role role = await database.Query<Role>(account.DefaultRoleId);
                    if (role == null || role.AccountId != account.Id)
                    {
                        Log.Error($"default role missing or owner mismatch: accountId: {account.Id} roleId: {account.DefaultRoleId}");
                        return RoleLoadResult.Failure("角色服务暂时不可用");
                    }

                    role.LastLoginTime = TimeInfo.Instance.ServerNow();
                    await database.Save(accountLockKey, role);
                    return RoleLoadResult.Success(ToSnapshot(role));
                }
                catch (Exception e)
                {
                    Log.Error($"role load storage error: accountKey: {accountLockKey}\n{e}");
                    return RoleLoadResult.Failure("角色服务暂时不可用");
                }
            }
        }

        public static async ETTask<bool> SaveProgress(Scene root, long roleId, string mapName, float3 position)
        {
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            bool saved=false;
            try
            {
                DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
                Role role = await database.Query<Role>(roleId);
                if (role == null)
                {
                    Log.Error($"save role not found: {roleId}");
                    return false;
                }

                role.MapName = mapName;
                role.PositionX = position.x;
                role.PositionY = position.y;
                role.PositionZ = position.z;
                role.LastSaveTime = TimeInfo.Instance.ServerNow();
                await database.Save(roleId, role);
                saved=true;
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"save role progress error: {roleId} map: {mapName}\n{e}");
                return false;
            }
            finally
            {
                OperationalMetrics.Instance?.RecordHandlerDuration(saved?HandlerLatency.RoleSaveSuccess:HandlerLatency.RoleSaveFailure,
                    (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
            }
        }

        private static async ETTask<RoleLoadResult> CreateDefaultRole(
            Scene root,
            DBComponent database,
            long accountLockKey,
            Account account)
        {
            Role role = root.AddChild<Role>();
            try
            {
                long now = TimeInfo.Instance.ServerNow();
                role.AccountId = account.Id;
                role.AccountName = account.LoginName;
                role.Name = account.DisplayName;
                role.MapName = "Map1";
                role.PositionX = -10;
                role.PositionY = 0;
                role.PositionZ = -10;
                role.CreateTime = now;
                role.LastLoginTime = now;
                role.LastSaveTime = now;

                if (Options.Instance.Develop == 0)
                {
                    await SaveRoleAndAccountTransaction(database, role, account);
                }
                else
                {
                    await SaveRoleAndAccountWithCompensation(database, accountLockKey, role, account);
                }
                return RoleLoadResult.Success(ToSnapshot(role));
            }
            finally
            {
                role.Dispose();
            }
        }

        private static async ETTask SaveRoleAndAccountTransaction(DBComponent database, Role role, Account account)
        {
            using IClientSessionHandle session = await database.mongoClient.StartSessionAsync();
            TransactionOptions transactionOptions = new(
                ReadConcern.Snapshot,
                ReadPreference.Primary,
                WriteConcern.WMajority);
            IMongoCollection<Role> roles = database.database.GetCollection<Role>(typeof(Role).FullName);
            IMongoCollection<Account> accounts = database.database.GetCollection<Account>(typeof(Account).FullName);
            await session.WithTransactionAsync(
                async (transactionSession, cancellationToken) =>
                {
                    await roles.ReplaceOneAsync(
                        transactionSession,
                        storedRole => storedRole.Id == role.Id,
                        role,
                        new ReplaceOptions { IsUpsert = true },
                        cancellationToken);
                    UpdateResult accountResult = await accounts.UpdateOneAsync(
                        transactionSession,
                        storedAccount => storedAccount.Id == account.Id && storedAccount.Status == AccountStatus.Active && storedAccount.DefaultRoleId == 0,
                        Builders<Account>.Update.Set(storedAccount => storedAccount.DefaultRoleId, role.Id),
                        new UpdateOptions { IsUpsert = false },
                        cancellationToken);
                    if (accountResult.MatchedCount != 1)
                    {
                        throw new InvalidOperationException($"account disappeared during role transaction: {account.Id}");
                    }
                    return true;
                },
                transactionOptions);
        }

        private static async ETTask SaveRoleAndAccountWithCompensation(
            DBComponent database,
            long accountLockKey,
            Role role,
            Account account)
        {
            bool roleSaved = false;
            try
            {
                await database.Save(accountLockKey, role);
                roleSaved = true;
                UpdateResult linked = await database.database.GetCollection<Account>(typeof(Account).FullName).UpdateOneAsync(
                    stored => stored.Id == account.Id && stored.Status == AccountStatus.Active && stored.DefaultRoleId == 0,
                    Builders<Account>.Update.Set(stored => stored.DefaultRoleId, role.Id));
                if (linked.MatchedCount != 1) throw new InvalidOperationException("Account changed during role creation");
            }
            catch
            {
                if (roleSaved)
                {
                    try
                    {
                        await database.Remove<Role>(accountLockKey, role.Id);
                    }
                    catch (Exception rollbackException)
                    {
                        Log.Error($"rollback orphan role failed: {role.Id}\n{rollbackException}");
                    }
                }
                throw;
            }
        }

        private static RoleSnapshot ToSnapshot(Role role)
        {
            return new RoleSnapshot(
                role.Id,
                role.Name,
                string.IsNullOrEmpty(role.MapName) ? "Map1" : role.MapName,
                new float3(role.PositionX, role.PositionY, role.PositionZ));
        }
    }
}

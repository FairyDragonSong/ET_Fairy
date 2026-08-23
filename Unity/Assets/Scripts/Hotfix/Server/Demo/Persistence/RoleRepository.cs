using System;
using System.Collections.Generic;
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
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"save role progress error: {roleId} map: {mapName}\n{e}");
                return false;
            }
        }

        private static async ETTask<RoleLoadResult> CreateDefaultRole(
            Scene root,
            DBComponent database,
            long accountLockKey,
            Account account)
        {
            Role role = root.AddChild<Role>();
            bool roleSaved = false;
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

                await database.Save(accountLockKey, role);
                roleSaved = true;
                account.DefaultRoleId = role.Id;
                await database.Save(accountLockKey, account);
                return RoleLoadResult.Success(ToSnapshot(role));
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
            finally
            {
                role.Dispose();
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

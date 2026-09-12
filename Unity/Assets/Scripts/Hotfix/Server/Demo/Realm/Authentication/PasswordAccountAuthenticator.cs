using System;
using System.Collections.Generic;
using MongoDB.Driver;

namespace ET.Server
{
    [EnableClass]
    [FriendOf(typeof(DBComponent))]
    public sealed class PasswordAccountAuthenticator: IAccountAuthenticator
    {
        public async ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request)
        {
            if (Options.Instance.PasswordLoginEnabled != 1)
            {
                return AccountAuthenticationResult.Failure(
                    ErrorCore.ERR_AuthenticationProviderDisabled,
                    "当前未开放账号密码登录");
            }

            if (!AccountInputValidator.TryNormalizeLoginName(request.Subject, out string loginName) ||
                !AccountInputValidator.IsPasswordValid(request.Credential))
            {
                return AccountAuthenticationResult.Failure(ErrorCore.ERR_AccountInputInvalid, "账号或密码格式错误");
            }

            long accountLockKey = loginName.GetLongHashCode();
            using (await root.GetComponent<CoroutineLockComponent>().Wait(CoroutineLockType.Account, accountLockKey))
            {
                try
                {
                    DBComponent database = root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
                    List<Account> accounts = await database.Query<Account>(accountLockKey, account => account.LoginName == loginName);
                    if (accounts.Count > 1)
                    {
                        Log.Error($"duplicate account records: accountKey: {accountLockKey} count: {accounts.Count}");
                        return AccountAuthenticationResult.Failure(
                            ErrorCore.ERR_AccountStorageUnavailable,
                            "账号服务暂时不可用");
                    }

                    if (accounts.Count == 0)
                    {
                        if (Options.Instance.AccountAutoRegister != 1)
                        {
                            PasswordHasher.SimulateVerification(request.Credential);
                            return InvalidCredentials();
                        }

                        return await Register(root, database, loginName, request.Subject.Trim(), request.Credential);
                    }

                    Account account = accounts[0];
                    if (account.Status != AccountStatus.Active)
                    {
                        PasswordHasher.SimulateVerification(request.Credential);
                        Log.Warning($"event=login_rejected reason=account_disabled account_id={account.Id}");
                        return InvalidCredentials();
                    }

                    if (!PasswordHasher.Verify(
                            request.Credential,
                            account.PasswordScheme,
                            account.PasswordSalt,
                            account.PasswordHash,
                            account.PasswordIterations))
                    {
                        return InvalidCredentials();
                    }

                    FilterDefinition<Account> versionFilter = Builders<Account>.Filter.Eq(stored => stored.StateVersion, account.StateVersion);
                    if (account.StateVersion == 0) versionFilter |= Builders<Account>.Filter.Exists(nameof(Account.StateVersion), false);
                    var identityFilter = Builders<Account>.Filter.Where(stored => stored.Id == account.Id && stored.Status == AccountStatus.Active &&
                            stored.PasswordHash == account.PasswordHash && stored.PasswordSalt == account.PasswordSalt &&
                            stored.PasswordIterations == account.PasswordIterations && stored.PasswordScheme == account.PasswordScheme);
                    UpdateResult updated = await database.database.GetCollection<Account>(typeof(Account).FullName)
                        .UpdateOneAsync(identityFilter & versionFilter,
                            Builders<Account>.Update.Max(stored => stored.LastLoginTime, TimeInfo.Instance.ServerNow()));
                    if (updated.MatchedCount != 1) return InvalidCredentials();
                    return AccountAuthenticationResult.Success(account.LoginName, account.StateVersion);
                }
                catch (Exception e)
                {
                    Log.Error($"account authentication storage error: accountKey: {accountLockKey}\n{e}");
                    return AccountAuthenticationResult.Failure(
                        ErrorCore.ERR_AccountStorageUnavailable,
                        "账号服务暂时不可用");
                }
            }
        }

        private static async ETTask<AccountAuthenticationResult> Register(
            Scene root,
            DBComponent database,
            string loginName,
            string displayName,
            string password)
        {
            PasswordHashData passwordHash = PasswordHasher.Hash(password);
            Account account = root.AddChild<Account>();
            try
            {
                long now = TimeInfo.Instance.ServerNow();
                account.LoginName = loginName;
                account.DisplayName = displayName;
                account.Status = AccountStatus.Active;
                account.PasswordScheme = passwordHash.Scheme;
                account.PasswordSalt = passwordHash.Salt;
                account.PasswordHash = passwordHash.Hash;
                account.PasswordIterations = passwordHash.Iterations;
                account.CreateTime = now;
                account.LastLoginTime = now;

                await database.Save(loginName.GetLongHashCode(), account);
                Log.Info($"event=password_account_registered account_id={account.Id}");
                return AccountAuthenticationResult.Success(loginName, account.StateVersion);
            }
            finally
            {
                account.Dispose();
            }
        }

        private static AccountAuthenticationResult InvalidCredentials()
        {
            return AccountAuthenticationResult.Failure(
                ErrorCore.ERR_InvalidAccountOrPassword,
                "账号或密码错误");
        }

    }
}

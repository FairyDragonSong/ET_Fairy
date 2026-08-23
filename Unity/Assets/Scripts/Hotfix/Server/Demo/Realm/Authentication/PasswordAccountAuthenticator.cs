using System;
using System.Collections.Generic;

namespace ET.Server
{
    [EnableClass]
    public sealed class PasswordAccountAuthenticator: IAccountAuthenticator
    {
        public async ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request)
        {
            if (!TryNormalizeLoginName(request.Subject, out string loginName) || !IsPasswordValid(request.Credential))
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
                        Log.Error($"duplicate account records: {loginName} count: {accounts.Count}");
                        return AccountAuthenticationResult.Failure(
                            ErrorCore.ERR_AccountStorageUnavailable,
                            "账号服务暂时不可用");
                    }

                    if (accounts.Count == 0)
                    {
                        if (Options.Instance.AccountAutoRegister != 1)
                        {
                            return InvalidCredentials();
                        }

                        return await Register(root, database, loginName, request.Subject.Trim(), request.Credential);
                    }

                    Account account = accounts[0];
                    if (account.Status != AccountStatus.Active)
                    {
                        return AccountAuthenticationResult.Failure(ErrorCore.ERR_AccountDisabled, "账号不可用");
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

                    account.LastLoginTime = TimeInfo.Instance.ServerNow();
                    await database.Save(accountLockKey, account);
                    return AccountAuthenticationResult.Success(account.LoginName);
                }
                catch (Exception e)
                {
                    Log.Error($"account authentication storage error: {loginName}\n{e}");
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
                Log.Info($"password account registered: {loginName} id: {account.Id}");
                return AccountAuthenticationResult.Success(loginName);
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

        private static bool TryNormalizeLoginName(string value, out string normalized)
        {
            normalized = value?.Trim().ToLowerInvariant();
            if (normalized == null || normalized.Length < 3 || normalized.Length > 32)
            {
                return false;
            }

            foreach (char c in normalized)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool IsPasswordValid(string password)
        {
            return password != null && password.Length >= 8 && password.Length <= 128;
        }
    }
}

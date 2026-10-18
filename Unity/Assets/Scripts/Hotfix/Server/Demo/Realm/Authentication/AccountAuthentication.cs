namespace ET.Server
{
    // 认证方式只决定凭据如何校验，成功后统一返回规范化账号。
    public enum AccountAuthenticationProvider
    {
        Password = 1,
        WeChat = 2,
    }

    /// <summary>
    /// 账号密码：
    //    Subject    = 账号名
    //    Credential = 密码
    //
    //   微信登录：
    //   Subject    = 微信标识或临时身份
    //   Credential = 微信 Code/Token
    /// </summary>
    public readonly struct AccountAuthenticationRequest
    {
        public AccountAuthenticationProvider Provider { get; }
        // 认证主体
        public string Subject { get; }
        // 认证凭据
        public string Credential { get; }

        private AccountAuthenticationRequest(AccountAuthenticationProvider provider, string subject, string credential)
        {
            this.Provider = provider;
            this.Subject = subject;
            this.Credential = credential;
        }

        public static AccountAuthenticationRequest Password(string account, string password)
        {
            return new AccountAuthenticationRequest(AccountAuthenticationProvider.Password, account, password);
        }
    }

    public readonly struct AccountAuthenticationResult
    {
        public bool IsSuccess { get; }

        public int Error { get; }

        public string Message { get; }

        public string LoginName { get; }
        public long StateVersion { get; }

        // 封装认证结果，并携带签发 Gate 凭据所需的账号版本。
        private AccountAuthenticationResult(bool isSuccess, int error, string message, string loginName, long stateVersion)
        {
            this.IsSuccess = isSuccess;
            this.Error = error;
            this.Message = message;
            this.LoginName = loginName;
            this.StateVersion = stateVersion;
        }

        // 构造角色加载成功结果。
        public static AccountAuthenticationResult Success(string loginName, long stateVersion)
        {
            return new AccountAuthenticationResult(true, 0, string.Empty, loginName, stateVersion);
        }

        public static AccountAuthenticationResult Failure(int error, string message)
        {
            return new AccountAuthenticationResult(false, error, message, string.Empty, 0);
        }
    }

    public interface IAccountAuthenticator
    {
        ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request);
    }

    /// <summary>
    /// 账号认证
    /// </summary>
    public static class AccountAuthentication
    {
        public static async ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request)
        {
            // 在入口按认证方式分发，方便后续接入微信等认证实现。
            IAccountAuthenticator authenticator = request.Provider switch
            {
                AccountAuthenticationProvider.Password => new PasswordAccountAuthenticator(),
                _ => null,
            };
            if (authenticator == null)
            {
                return AccountAuthenticationResult.Failure(
                    ErrorCore.ERR_InvalidAccountOrPassword,
                    "不支持的登录方式");
            }

            return await authenticator.Authenticate(root, request);
        }
    }
}

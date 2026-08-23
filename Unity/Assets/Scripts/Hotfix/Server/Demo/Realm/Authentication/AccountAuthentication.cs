namespace ET.Server
{
    public enum AccountAuthenticationProvider
    {
        Password = 1,
        WeChat = 2,
    }

    public readonly struct AccountAuthenticationRequest
    {
        public AccountAuthenticationProvider Provider { get; }

        public string Subject { get; }

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

        private AccountAuthenticationResult(bool isSuccess, int error, string message, string loginName)
        {
            this.IsSuccess = isSuccess;
            this.Error = error;
            this.Message = message;
            this.LoginName = loginName;
        }

        public static AccountAuthenticationResult Success(string loginName)
        {
            return new AccountAuthenticationResult(true, 0, string.Empty, loginName);
        }

        public static AccountAuthenticationResult Failure(int error, string message)
        {
            return new AccountAuthenticationResult(false, error, message, string.Empty);
        }
    }

    public interface IAccountAuthenticator
    {
        ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request);
    }

    public static class AccountAuthentication
    {
        public static async ETTask<AccountAuthenticationResult> Authenticate(Scene root, AccountAuthenticationRequest request)
        {
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

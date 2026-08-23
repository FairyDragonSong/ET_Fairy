namespace ET.Server
{
    public enum AccountStatus
    {
        Active = 0,
        Disabled = 1,
    }

    public enum PasswordHashScheme
    {
        Pbkdf2Sha256 = 1,
    }

    [ChildOf(typeof(Scene))]
    public sealed class Account: Entity, IAwake
    {
        public string LoginName { get; set; }

        public string DisplayName { get; set; }

        public AccountStatus Status { get; set; }

        public PasswordHashScheme PasswordScheme { get; set; }

        public string PasswordSalt { get; set; }

        public string PasswordHash { get; set; }

        public int PasswordIterations { get; set; }

        public long CreateTime { get; set; }

        public long LastLoginTime { get; set; }

        public long DefaultRoleId { get; set; }
    }
}

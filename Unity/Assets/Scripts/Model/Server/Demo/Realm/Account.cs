namespace ET.Server
{
    public enum AccountStatus
    {
        Active = 0,
        Disabled = 1,
        Closed = 2,
    }

    public enum PasswordHashScheme
    {
        Pbkdf2Sha256 = 1,
    }

    [ChildOf(typeof(Scene))]
    public sealed class Account: Entity, IAwake
    {
        // 规范化后的唯一登录名；DisplayName 保留用户输入的展示形式。
        public string LoginName { get; set; }

        public string DisplayName { get; set; }

        public AccountStatus Status { get; set; }

        public long StateVersion { get; set; }
        public long ClosedAt { get; set; }

        // 密码算法、盐、哈希和迭代次数共同组成可升级的密码凭据。
        public PasswordHashScheme PasswordScheme { get; set; }

        public string PasswordSalt { get; set; }

        public string PasswordHash { get; set; }

        public int PasswordIterations { get; set; }

        public long CreateTime { get; set; }

        public long LastLoginTime { get; set; }

        public long DefaultRoleId { get; set; }
    }
}

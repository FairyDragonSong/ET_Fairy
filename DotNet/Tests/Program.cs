using ET.Server;
using Unity.Mathematics;

namespace ET.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action Run)> Tests =
    [
        ("账号输入规范化与密码边界", TestAccountInput),
        ("PBKDF2 哈希、随机盐与错误密码", TestPasswordHashing),
        ("Gate Key 只能消费一次", TestOneTimeGateKey),
        ("Gate Key 使用密码学随机源", TestSecureGateKey),
        ("外网消息与字节限流", TestTrafficLimit),
        ("登录 IP 与账号爆破限流", TestLoginRateLimit),
        ("角色快照保留地图与坐标", TestRoleSnapshot),
        ("停机参与者成功与失败汇总", TestShutdownCoordinator),
        ("商业模式启动配置门禁", TestProductionStartupValidation),
        ("认证敏感消息统一标记", TestSensitiveMessages),
        ("登录与持久化安全架构回归", TestSourceGuards),
    ];

    private static int Main()
    {
        World.Instance.AddSingleton(new Options
        {
            OuterMessageLimit = 2,
            OuterBytesLimit = 10,
            LoginIpLimit = 2,
            LoginAccountLimit = 2,
            LoginRateWindowSeconds = 60,
        });
        World.Instance.AddSingleton<TimeInfo>();
        World.Instance.AddSingleton<ObjectPool>();

        int failures = 0;
        foreach ((string name, Action run) in Tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception e)
            {
                ++failures;
                Console.Error.WriteLine($"FAIL {name}: {e.Message}");
            }
        }

        World.Instance.Dispose();
        Console.WriteLine($"RESULT total={Tests.Count} passed={Tests.Count - failures} failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void TestAccountInput()
    {
        Assert(AccountInputValidator.TryNormalizeLoginName("  Player_01 ", out string normalized), "valid account rejected");
        Assert(normalized == "player_01", "account was not normalized");
        Assert(!AccountInputValidator.TryNormalizeLoginName("ab", out _), "short account accepted");
        Assert(!AccountInputValidator.TryNormalizeLoginName("player@example", out _), "invalid character accepted");
        Assert(AccountInputValidator.IsPasswordValid("12345678"), "valid password rejected");
        Assert(!AccountInputValidator.IsPasswordValid("1234567"), "short password accepted");
    }

    private static void TestPasswordHashing()
    {
        PasswordHashData first = PasswordHasher.Hash("correct horse battery staple");
        PasswordHashData second = PasswordHasher.Hash("correct horse battery staple");
        Assert(first.Salt != second.Salt && first.Hash != second.Hash, "password hashes reused salt");
        Assert(PasswordHasher.Verify("correct horse battery staple", first.Scheme, first.Salt, first.Hash, first.Iterations), "correct password rejected");
        Assert(!PasswordHasher.Verify("wrong password", first.Scheme, first.Salt, first.Hash, first.Iterations), "wrong password accepted");
        Assert(!PasswordHasher.Verify("correct horse battery staple", first.Scheme, first.Salt, first.Hash, 1), "unsafe iteration count accepted");
        Assert(!PasswordHasher.Verify("correct horse battery staple", first.Scheme, "not-base64", first.Hash, first.Iterations), "invalid salt accepted");
    }

    private static void TestOneTimeGateKey()
    {
        GateSessionKeyComponent keys = new();
        keys.sessionKey[42] = new GateLoginTicket("player_01", 7);
        GateLoginTicket ticket = keys.Take(42);
        Assert(ticket.Account == "player_01" && ticket.StateVersion == 7, "first key consumption or account version failed");
        Assert(keys.Take(42) == null, "replayed key was accepted");
        R2G_GetLoginKey message = new() { Account = "player_01", AccountStateVersion = 7, RpcId = 12 };
        byte[] bytes = MemoryPack.MemoryPackSerializer.Serialize(message);
        R2G_GetLoginKey copy = MemoryPack.MemoryPackSerializer.Deserialize<R2G_GetLoginKey>(bytes);
        Assert(copy.AccountStateVersion == 7 && copy.Account == message.Account && copy.RpcId == 12, "internal ticket version serialization failed");
        R2G_GetLoginKey pooled = R2G_GetLoginKey.Create(true);
        pooled.AccountStateVersion = 99;
        pooled.Account = "previous";
        pooled.Dispose();
        using R2G_GetLoginKey reused = R2G_GetLoginKey.Create(true);
        Assert(reused.AccountStateVersion == 0 && reused.Account == null, "pooled authentication data was retained");
    }

    private static void TestTrafficLimit()
    {
        SessionTrafficLimitComponent traffic = new() { WindowStartTime = TimeInfo.Instance.ClientNow() };
        Assert(traffic.TryConsume(4), "first packet rejected");
        Assert(traffic.TryConsume(6), "second packet rejected");
        Assert(!traffic.TryConsume(1), "message count limit not enforced");
        traffic.WindowStartTime = TimeInfo.Instance.ClientNow();
        traffic.MessageCount = 0;
        traffic.ByteCount = 0;
        Assert(!traffic.TryConsume(11), "byte limit not enforced");
    }

    private static void TestSecureGateKey()
    {
        HashSet<long> keys = [];
        for (int i = 0; i < 1000; ++i)
        {
            long key = GateSessionKeyComponentSystem.GenerateSecureKey();
            Assert(key >= 0, "secure Gate key was outside protocol range");
            Assert(keys.Add(key), "secure Gate key collision in sample");
        }
    }

    private static void TestRoleSnapshot()
    {
        RoleSnapshot role = new(1001, "角色", "Map2", new float3(1.25f, 2.5f, -3.75f));
        Assert(role.Id == 1001 && role.MapName == "Map2", "role identity or map changed");
        Assert(role.Position.Equals(new float3(1.25f, 2.5f, -3.75f)), "role position changed");
    }

    private static void TestLoginRateLimit()
    {
        LoginRateLimiterComponent limiter = new() { NextCleanupTime = long.MaxValue };
        Assert(limiter.TryAcquire("10.0.0.1", "player_01", out _), "first login rejected");
        Assert(limiter.TryAcquire("10.0.0.1", "player_01", out _), "second login rejected");
        Assert(!limiter.TryAcquire("10.0.0.1", "player_01", out long retryAfter), "IP/account limit not enforced");
        Assert(retryAfter > 0, "rate limit did not return retry time");

        LoginRateLimiterComponent accountLimiter = new() { NextCleanupTime = long.MaxValue };
        Assert(accountLimiter.TryAcquire("10.0.0.2", "shared_01", out _), "first shared-account login rejected");
        Assert(accountLimiter.TryAcquire("10.0.0.3", "shared_01", out _), "second shared-account login rejected");
        Assert(!accountLimiter.TryAcquire("10.0.0.4", "shared_01", out _), "account limit across IPs not enforced");
    }

    private static void TestShutdownCoordinator()
    {
        ShutdownCoordinator shutdown = new();
        shutdown.Awake();
        shutdown.RegisterParticipant();
        shutdown.RegisterParticipant();
        shutdown.Request();
        Assert(!shutdown.IsCompleted, "shutdown completed before participants");
        shutdown.CompleteParticipant(true);
        Assert(!shutdown.IsCompleted, "shutdown completed after one participant");
        shutdown.CompleteParticipant(false);
        Assert(shutdown.IsCompleted && !shutdown.IsSuccessful, "shutdown failure was hidden");
    }

    private static void TestProductionStartupValidation()
    {
        DatabaseConnectionSettings secureDatabase = new(
            "mongodb://game:secret@db1:27017,db2:27017/?tls=true&replicaSet=rs0&retryWrites=true",
            "ET_PROD",
            true);
        ProductionStartupValidator.ValidateRealm(new Options { Develop = 0 }, secureDatabase);

        AssertThrows(
            () => ProductionStartupValidator.ValidateRealm(
                new Options { Develop = 0, AccountAutoRegister = 1 },
                secureDatabase),
            "production auto registration was accepted");
        AssertThrows(
            () => ProductionStartupValidator.ValidateRealm(
                new Options { Develop = 0, PasswordLoginEnabled = 1 },
                secureDatabase),
            "password login without secure transport assertion was accepted");
        AssertThrows(
            () => ProductionStartupValidator.ValidateRealm(
                new Options { Develop = 0 },
                new DatabaseConnectionSettings("mongodb://db1:27017/?tls=true&replicaSet=rs0", "ET", true)),
            "unauthenticated production MongoDB was accepted");
        AssertThrows(
            () => ProductionStartupValidator.ValidateRealm(
                new Options { Develop = 0 },
                new DatabaseConnectionSettings("mongodb://game:secret@db1:27017/?replicaSet=rs0", "ET", true)),
            "non-TLS production MongoDB was accepted");
        AssertThrows(
            () => ProductionStartupValidator.ValidateRealm(
                new Options { Develop = 0 },
                new DatabaseConnectionSettings("mongodb://game:secret@db1:27017/?tls=true", "ET", true)),
            "standalone production MongoDB was accepted");

        ProductionStartupValidator.ValidateRealm(
            new Options { Develop = 1, AccountAutoRegister = 1, PasswordLoginEnabled = 1 },
            new DatabaseConnectionSettings("mongodb://127.0.0.1", "ET_DEV", false));
    }

    private static void TestSensitiveMessages()
    {
        object[] messages =
        [
            new Main2NetClient_Login(),
            new C2M_Reload(),
            new C2R_Login(),
            new R2C_Login(),
            new C2G_LoginGate(),
            new R2G_GetLoginKey(),
            new G2R_GetLoginKey(),
        ];
        Assert(messages.All(message => message is ISensitiveMessage), "an authentication message is not marked sensitive");
    }

    private static void TestSourceGuards()
    {
        string root = FindRepositoryRoot();
        string realmLogin = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Realm/C2R_LoginHandler.cs");
        string gateLogin = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Gate/C2G_LoginGateHandler.cs");
        string disconnectProto = Read(root, "Unity/Assets/Config/Proto/InnerMessage_S_20001.proto");
        string migration = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Persistence/DatabaseMigration.cs");
        string shutdown = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Observability/GracefulShutdownComponentSystem.cs");
        string sessionLifecycle = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Gate/SessionPlayerComponentSystem.cs");
        string roleRepository = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Persistence/RoleRepository.cs");
        string enterMap = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Gate/C2G_EnterMapHandler.cs");
        string keyHandler = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Gate/R2G_GetLoginKeyHandler.cs");
        string passwordAuthenticator = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Realm/Authentication/PasswordAccountAuthenticator.cs");
        string realmInit = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/Realm/FiberInit_Realm.cs");
        string serverStartup = Read(root, "Unity/Assets/Scripts/Hotfix/Server/Demo/EntryEvent2_InitServer.cs");
        string databaseAudit = Read(root, "Tools/ServerOps/Audit-Mongo.js");
        string messageLogger = Read(root, "Unity/Assets/Scripts/Model/Share/Module/Message/LogMsg.cs");
        string sessionMonitor = Read(root, "DotNet/Hotfix/AccountSessionMonitorSystem.cs");

        Assert(!System.Text.RegularExpressions.Regex.IsMatch(realmLogin, @"Log\.\w+\([^;\r\n]*Password"), "password logging returned");
        Assert(gateLogin.Contains(".Take(request.Key)"), "Gate login no longer consumes one-time key");
        Assert(keyHandler.Contains(".Issue(request.Account, request.AccountStateVersion)") && !keyHandler.Contains("RandomGenerator.RandInt64"), "Gate key lost version binding or cryptographic randomness");
        Assert(gateLogin.Contains("accounts[0].StateVersion != ticket.StateVersion") &&
               realmLogin.Contains("r2GGetLoginKey.AccountStateVersion = authenticationResult.StateVersion"), "authenticated version no longer reaches Gate validation");
        Assert(passwordAuthenticator.Contains("PasswordHasher.SimulateVerification(request.Credential)"), "unknown-account timing equalization removed");
        Assert(passwordAuthenticator.Contains("PasswordLoginEnabled != 1"), "password provider fail-closed gate removed");
        Assert(passwordAuthenticator.Contains("stored.Status == AccountStatus.Active") &&
               passwordAuthenticator.Contains("stored.PasswordHash == account.PasswordHash") &&
               passwordAuthenticator.Contains("Builders<Account>.Update.Max") &&
               !passwordAuthenticator.Contains("database.Save(accountLockKey, account)"), "login can overwrite concurrent account changes");
        Assert(gateLogin.Contains("accounts[0].Status != AccountStatus.Active"), "Gate no longer rechecks account state");
        Assert(gateLogin.Contains("player.AccountStateVersion = accountStateVersion") &&
               sessionMonitor.Contains("matches[0].StateVersion!=player.AccountStateVersion") &&
               sessionMonitor.Contains("Session!=session") && sessionMonitor.Contains("now-player.AccountVerifiedAt>120000"),
               "Account session version, connection ownership or lease protection removed");
        Assert(roleRepository.Contains("storedAccount.DefaultRoleId == 0") &&
               roleRepository.Contains("Builders<Account>.Update.Set") &&
               !roleRepository.Contains("accounts.ReplaceOneAsync") &&
               !roleRepository.Contains("database.Save(accountLockKey, account)"), "role creation can overwrite concurrent account changes");
        Assert(realmInit.IndexOf("ProductionStartupValidator.ValidateRealm", StringComparison.Ordinal) <
               realmInit.IndexOf("DatabaseMigration.Apply", StringComparison.Ordinal), "production validation no longer runs before database startup");
        Assert(serverStartup.Contains("StartupCoordinator.Instance.MarkFailed()"), "required Fiber startup failures no longer terminate the process");
        Assert(gateLogin.Contains("CoroutineLockType.PlayerSession") && gateLogin.Contains("BindSession(player, session, accountStateVersion)"), "duplicate-login serialization or session binding removed");
        Assert(gateLogin.Contains("DetachOldSession(player)") && gateLogin.Contains("ApplyRoleSnapshot(player"), "old-session eviction or persisted snapshot reload removed");
        Assert(sessionLifecycle.Contains("playerSessionComponent.Session != self.GetParent<Session>()") && sessionLifecycle.Contains("PlayerOfflineComponent"), "old-session protection or reconnect retention removed");
        Assert(sessionLifecycle.Contains("TryReleaseMapState") && sessionLifecycle.Contains("CoroutineLockType.PlayerSession"), "disconnect save and reconnect are no longer serialized");
        Assert(enterMap.Contains("ERR_PlayerStateConflict") && enterMap.Contains("TransferAndCleanup"), "repeated enter-map protection or temporary scene cleanup removed");
        Assert(disconnectProto.Contains("G2M_SessionDisconnect // ILocationRequest") && disconnectProto.Contains("M2G_SessionDisconnect // ILocationResponse"), "disconnect save acknowledgement removed");
        Assert(roleRepository.Contains("account.DefaultRoleId") && roleRepository.Contains("SaveProgress"), "stable role reload/save path removed");
        Assert(roleRepository.Contains("SaveRoleAndAccountTransaction") && roleRepository.Contains("WithTransactionAsync"), "production account-role transaction removed");
        Assert(roleRepository.Contains("WriteConcern.WMajority") && roleRepository.Contains("ReadConcern.Snapshot"), "production transaction durability weakened");
        Assert(databaseAudit.Contains("accountsMissingRoles") && databaseAudit.Contains("orphanRoles") && databaseAudit.Contains("quit(2)"), "account-role consistency audit weakened");
        Assert(messageLogger.Contains("msg is ISensitiveMessage") && messageLogger.Contains("sensitive payload redacted"), "framework-level sensitive message redaction removed");
        Assert(migration.Contains("ux_account_login_name") && migration.Contains("ux_role_account_id"), "required unique indexes removed");
        Assert(shutdown.Contains("roleSaveComponent.SaveNow()") && shutdown.Contains("CompleteParticipant(success)"), "shutdown save coordination removed");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ET.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("repository root not found");
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}

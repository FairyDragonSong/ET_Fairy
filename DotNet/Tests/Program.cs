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
        ("管理签名与权限边界", TestAdminAuthorization),
        ("账号生命周期状态机", TestAccountStatePolicy),
        ("密码加密信封", TestPasswordEnvelope),
        ("自助改密混合加密与有效期", TestSelfPasswordCipher),
        ("安全指标固定基数与并发计数", TestSecurityMetrics),
        ("处理耗时直方图边界与并发", TestHandlerLatency),
        ("Fiber 投递队列积压快照", TestMessageQueueSnapshot),
        ("协程锁等待计数与终态去重", TestCoroutineLockMetrics),
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
        World.Instance.AddSingleton<StartupCoordinator>();
        World.Instance.AddSingleton<ShutdownCoordinator>();

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
            new C2G_ChangePassword(),
            new G2C_ChangePassword(),
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

    private static void TestAdminAuthorization()
    {
        var limiter=new AccountAdminRateLimiter(2,60000);
        Assert(limiter.TryAcquire(0)&&limiter.TryAcquire(1)&&!limiter.TryAcquire(59999),"Admin rate limit not enforced");
        Assert(limiter.TryAcquire(60000)&&!limiter.TryAcquire(60000)&&limiter.TryAcquire(60001),"Admin rolling window recovery incorrect");
        using var rsa=System.Security.Cryptography.RSA.Create(3072);
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var identity=new AccountAdminOperator {Id="test-operator",PublicKeyPem=rsa.ExportSubjectPublicKeyInfoPem(),Database="test",DeploymentId="test-deployment",Zone=1,Actions=["disable"],ExpiresAt=now+3600};
        string trust=System.Text.Json.JsonSerializer.Serialize(new[]{identity});
        var command=new AccountAdminCommand {OperatorId=identity.Id,Database="test",DeploymentId="test-deployment",Zone=1,Action="disable",AccountId=123,ConfirmAccountId=123,ExpectedVersion=0,OperationId=Guid.NewGuid().ToString("N"),ReasonCode="test",IssuedAt=now,ExpiresAt=now+300};
        string Sign()
        {
            byte[] payload=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command);
            return System.Text.Json.JsonSerializer.Serialize(new AccountAdminEnvelope {OperatorId=identity.Id,Payload=Convert.ToBase64String(payload),Signature=Convert.ToBase64String(rsa.SignData(payload,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pss))});
        }
        string valid=Sign();
        Assert(AccountAdminAuthorization.Verify(valid,trust,now).AccountId==123,"valid signature rejected");
        AssertThrows(()=>AccountAdminAuthorization.Verify(valid,trust,now+301),"expired command accepted");
        command.Action="enable";
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"unauthorized action accepted");
        command.Action="close";
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"ungranted close permission accepted");
        command.Action="revoke";
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"ungranted revoke permission accepted");
        command.Action="disable";command.Database="production";
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"wrong database accepted");
        command.Database="test";command.DeploymentId="another-deployment";
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"wrong deployment accepted");
        command.DeploymentId="test-deployment";command.ConfirmAccountId=124;
        AssertThrows(()=>AccountAdminAuthorization.Verify(Sign(),trust,now),"missing account confirmation accepted");
        command.ConfirmAccountId=123;
        var envelope=System.Text.Json.JsonSerializer.Deserialize<AccountAdminEnvelope>(valid);
        byte[] altered=Convert.FromBase64String(envelope.Payload);altered[0]^=1;
        envelope.Payload=Convert.ToBase64String(altered);
        AssertThrows(()=>AccountAdminAuthorization.Verify(System.Text.Json.JsonSerializer.Serialize(envelope),trust,now),"tampered payload accepted");
        AssertThrows(()=>AccountAdminAuthorization.Verify(valid,"[]",now),"removed operator accepted");
    }

    private static void TestAccountStatePolicy()
    {
        Assert(AccountStatePolicy.Target(AccountStatus.Active,"revoke")==AccountStatus.Active,"revoke disabled account");
        Assert(AccountStatePolicy.Target(AccountStatus.Disabled,"revoke")==AccountStatus.Disabled,"revoke enabled disabled account");
        Assert(AccountStatePolicy.Target(AccountStatus.Active,"close")==AccountStatus.Closed,"close transition failed");
        Assert(AccountStatePolicy.Target(AccountStatus.Disabled,"close")==AccountStatus.Closed,"disabled close transition failed");
        foreach(string action in new[]{"enable","disable","revoke","close"})
            AssertThrows(()=>AccountStatePolicy.Target(AccountStatus.Closed,action),"closed account changed state");
        AssertThrows(()=>AccountStatePolicy.Target(AccountStatus.Active,"enable"),"no-op enable accepted");
        Assert(AccountStatePolicy.Target(AccountStatus.Disabled,"reset-password")==AccountStatus.Disabled,"reset enabled disabled account");
        AssertThrows(()=>AccountStatePolicy.Target(AccountStatus.Closed,"reset-password"),"reset reopened closed account");
    }

    private static void TestPasswordEnvelope()
    {
        using var rsa=System.Security.Cryptography.RSA.Create(4096);
        const string password="test-password-envelope";
        string Encrypt()=>Convert.ToBase64String(rsa.Encrypt(System.Text.Encoding.UTF8.GetBytes(password),System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));
        string first=Encrypt(),second=Encrypt();
        Assert(first!=second,"password encryption is not randomized");
        var hash=AccountPasswordEnvelope.DecryptAndHash(first,rsa.ExportPkcs8PrivateKeyPem());
        Assert(PasswordHasher.Verify(password,hash.Scheme,hash.Salt,hash.Hash,hash.Iterations),"encrypted password cannot authenticate");
        Assert(!PasswordHasher.Verify("wrong-password",hash.Scheme,hash.Salt,hash.Hash,hash.Iterations),"wrong encrypted password accepted");
        byte[] tampered=Convert.FromBase64String(first);tampered[0]^=1;
        bool rejected=false;
        try {AccountPasswordEnvelope.DecryptAndHash(Convert.ToBase64String(tampered),rsa.ExportPkcs8PrivateKeyPem());}
        catch(System.Security.Cryptography.CryptographicException){rejected=true;}
        Assert(rejected,"tampered ciphertext decrypted");
    }

    private static void TestSelfPasswordCipher()
    {
        using var rsa=System.Security.Cryptography.RSA.Create(4096);
        string privateKey=rsa.ExportPkcs8PrivateKeyPem(),publicKey=rsa.ExportSubjectPublicKeyInfoPem();
        var request=new SelfPasswordChange {LoginName="test_user",OperationId=Guid.NewGuid().ToString("N"),OldPassword=new string('旧',128),NewPassword=new string('新',128),ExpiresAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+240};
        string envelope=SelfPasswordCipher.Encrypt(request,publicKey);
        var copy=SelfPasswordCipher.Decrypt(envelope,privateKey);
        Assert(copy.OldPassword==request.OldPassword&&copy.NewPassword==request.NewPassword&&copy.LoginName==request.LoginName,"Unicode boundary roundtrip failed");
        Assert(envelope!=SelfPasswordCipher.Encrypt(request,publicKey),"Encryption reused randomness");
        var data=System.Text.Json.JsonSerializer.Deserialize<PasswordChangeCipher>(envelope)!;
        byte[] tag=Convert.FromBase64String(data.Tag);tag[0]^=1;data.Tag=Convert.ToBase64String(tag);
        bool rejected=false;
        try {SelfPasswordCipher.Decrypt(System.Text.Json.JsonSerializer.Serialize(data),privateKey);}
        catch(System.Security.Cryptography.CryptographicException){rejected=true;}
        Assert(rejected,"Modified GCM tag accepted");
        request.ExpiresAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-1;
        AssertThrows(()=>SelfPasswordCipher.Decrypt(SelfPasswordCipher.Encrypt(request,publicKey),privateKey),"Expired request accepted");
        request.ExpiresAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+600;
        AssertThrows(()=>SelfPasswordCipher.Decrypt(SelfPasswordCipher.Encrypt(request,publicKey),privateKey),"Excessive lifetime accepted");
        using var message=new C2G_ChangePassword {Envelope=envelope,RpcId=42};
        Assert(message is ISessionRequest && new G2C_ChangePassword() is ISessionResponse,"Change password must route to Gate session handler");
        var decoded=MemoryPack.MemoryPackSerializer.Deserialize<C2G_ChangePassword>(MemoryPack.MemoryPackSerializer.Serialize(message));
        Assert(decoded.Envelope==envelope&&decoded.RpcId==42,"Protocol roundtrip failed");
    }

    private static void TestSecurityMetrics()
    {
        var metrics=new OperationalMetrics();metrics.Awake();
        Parallel.For(0,1000,_=>metrics.RecordSecurity(SecurityMetric.PasswordChangeAccepted));
        Parallel.For(0,1000,_=>{metrics.RecordGateSession(true);metrics.RecordGateSession(false);});
        foreach(var metric in Enum.GetValues<SecurityMetric>().Where(v=>v!=SecurityMetric.Count))metrics.RecordSecurity(metric);
        string text=metrics.ExportPrometheus();
        Assert(text.Contains($"et_process_id {Environment.ProcessId}\n")&&text.Contains($"et_config_process_id {Options.Instance.Process}\n"),"Metric process identity incorrect");
        Assert(text.Contains("et_password_change_accepted_total 1001\n"),"Concurrent security increments lost");
        Assert(text.Contains("# TYPE et_session_journal_end_failures_total counter\n"),"Counter type metadata missing");
        Assert(text.Split('\n').Where(l=>l.Contains('{')).All(l=>System.Text.RegularExpressions.Regex.IsMatch(l,"^et_((realm_login|gate_login|password_change)_handler|role_save_(success|failure)|map_(ordered|unordered)_mailbox)_seconds_bucket\\{le=\"(\\+Inf|[0-9.]+)\"\\} [0-9]+$")),"Unexpected dynamic metric labels");
        Assert(text.Split('\n').Count(l=>l.StartsWith("# TYPE "))==(int)SecurityMetric.Count+22,"Metric count changed unexpectedly");
        Assert(text.Contains("# TYPE et_gate_bound_sessions gauge\n")&&text.Contains("et_gate_bound_sessions 0\n")&&
            text.Contains("et_gate_session_bindings_total 1000\n")&&text.Contains("et_gate_session_unbindings_total 1000\n"),"Concurrent bound-session accounting incorrect");
        bool rejected=false;
        try {metrics.RecordSecurity((SecurityMetric)999);}
        catch(ArgumentOutOfRangeException){rejected=true;}
        Assert(rejected,"Unknown metric accepted");
    }

    private static void TestHandlerLatency()
    {
        var metrics=new OperationalMetrics();metrics.Awake();
        Parallel.For(0,1000,_=> { metrics.StartMapMailbox(true); metrics.FinishMapMailbox(true, false); });
        metrics.StartMapMailbox(false);
        Assert(metrics.ExportPrometheus().Contains("et_map_unordered_mailbox_inflight 1\n"), "Unfinished mailbox invisible");
        metrics.FinishMapMailbox(false, true);
        string inflight = metrics.ExportPrometheus();
        Assert(inflight.Contains("et_map_ordered_mailbox_inflight 0\n") && inflight.Contains("et_map_unordered_mailbox_inflight 0\n") &&
            inflight.Contains("et_map_unordered_mailbox_exceptions_total 1\n") && inflight.Contains("et_map_ordered_mailbox_exceptions_total 0\n"), "Mailbox lifecycle accounting incorrect");
        foreach(double value in new[]{0.0,1,2,6000})metrics.RecordHandlerDuration(HandlerLatency.RealmLogin,value);
        Parallel.For(0,1000,_=>metrics.RecordHandlerDuration(HandlerLatency.GateLogin,5));
        metrics.RecordHandlerDuration(HandlerLatency.RoleSaveSuccess,10);
        metrics.RecordHandlerDuration(HandlerLatency.RoleSaveFailure,6000);
        metrics.RecordHandlerDuration(HandlerLatency.MapOrderedMailbox,25);
        metrics.RecordHandlerDuration(HandlerLatency.MapUnorderedMailbox,50);
        var culture=System.Globalization.CultureInfo.CurrentCulture;
        string text;
        try {System.Globalization.CultureInfo.CurrentCulture=new System.Globalization.CultureInfo("fr-FR");text=metrics.ExportPrometheus();}
        finally {System.Globalization.CultureInfo.CurrentCulture=culture;}
        Assert(text.Contains("et_realm_login_handler_seconds_bucket{le=\"0.001\"} 2\n")&&
            text.Contains("et_realm_login_handler_seconds_bucket{le=\"5\"} 3\n")&&
            text.Contains("et_realm_login_handler_seconds_bucket{le=\"+Inf\"} 4\n"),"Cumulative latency buckets incorrect");
        Assert(text.Contains("et_realm_login_handler_seconds_count 4\n")&&text.Contains("et_realm_login_handler_seconds_sum 6.003\n"),"Latency sum/count or invariant formatting incorrect");
        Assert(text.Contains("et_gate_login_handler_seconds_count 1000\n")&&text.Contains("et_gate_login_handler_seconds_sum 5\n"),"Concurrent latency observations lost");
        Assert(text.Contains("et_map_ordered_mailbox_seconds_count 1\n") && text.Contains("et_map_ordered_mailbox_seconds_sum 0.025\n") &&
            text.Contains("et_map_unordered_mailbox_seconds_count 1\n") && text.Contains("et_map_unordered_mailbox_seconds_sum 0.05\n"), "Map mailbox samples mixed");
        Assert(text.Contains("et_role_save_success_seconds_count 1\n")&&text.Contains("et_role_save_failure_seconds_count 1\n")&&
            text.Contains("et_role_save_failure_seconds_sum 6\n"),"Role save outcomes mixed or omitted");
        foreach(double invalid in new[]{-1.0,double.NaN,double.PositiveInfinity})
        {
            bool rejected=false;
            try {metrics.RecordHandlerDuration(HandlerLatency.GateLogin,invalid);}
            catch(ArgumentOutOfRangeException){rejected=true;}
            Assert(rejected,"Invalid latency sample accepted");
        }
    }

    private static void TestCoroutineLockMetrics()
    {
        var before = CoroutineLockMetrics.GetSnapshot();
        var waiter = WaitCoroutineLock.Create();
        var task = waiter.Wait();
        Assert(CoroutineLockMetrics.GetSnapshot().Waiting == before.Waiting + 1, "waiting not counted");
        waiter.SetResult(null);
        task.GetAwaiter().GetResult();

        var failed = WaitCoroutineLock.Create();
        var failedTask = failed.Wait();
        failed.SetException(new InvalidOperationException("expected lock failure"));
        bool caught = false;
        try { failedTask.GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { caught = true; }
        Assert(caught, "exception propagation changed");

        var abandoned = WaitCoroutineLock.Create();
        var abandonedTask = abandoned.Wait();
        abandoned.AbandonTracking();
        abandoned.AbandonTracking();
        abandoned.SetResult(null);
        abandonedTask.GetAwaiter().GetResult();
        Parallel.For(0, 1000, _ =>
        {
            var item = WaitCoroutineLock.Create();
            var pending = item.Wait();
            item.SetResult(null);
            pending.GetAwaiter().GetResult();
        });
        var after = CoroutineLockMetrics.GetSnapshot();
        Assert(after.Waiting == before.Waiting, "waiting leaked or double decremented");
        Assert(after.Started - before.Started == 1003 && after.Acquired - before.Acquired == 1001 &&
               after.Failed - before.Failed == 1 && after.Abandoned - before.Abandoned == 1,
               "lock terminal counts incorrect");
    }

    private static void TestMessageQueueSnapshot()
    {
        var queue=new MessageQueue();queue.Awake();
        queue.AddQueue(100);queue.AddQueue(200);
        var first=new ActorId(1,100,1);
        var second=new ActorId(1,200,1);
        Parallel.For(0,1000,_=>Assert(queue.Send(first,null),"Registered queue rejected message"));
        for(int i=0;i<3;i++)Assert(queue.Send(second,null),"Second queue rejected message");
        var snapshot=queue.GetSnapshot();
        Assert(snapshot.Queues==2&&snapshot.Pending==1003&&snapshot.Maximum==1000,"Pending snapshot incorrect");
        var batch=new List<MessageInfo>();queue.Fetch(100,10,batch);
        Assert(batch.Count==10&&queue.GetSnapshot().Pending==993,"Fetched messages still counted as pending");
        queue.RemoveQueue(200);
        Assert(!queue.Send(second,null)&&queue.GetSnapshot().Pending==990&&queue.GetSnapshot().Queues==1,"Removed queue remains in snapshot");
        int fetched=0;
        Parallel.For(0,100,_=>{var items=new List<MessageInfo>();queue.Fetch(100,10,items);Interlocked.Add(ref fetched,items.Count);});
        Assert(fetched==990&&queue.GetSnapshot().Pending==0&&queue.GetSnapshot().Maximum==0,"Concurrent fetch accounting incorrect");
        queue.RemoveQueue(100);
        Assert(queue.GetSnapshot().Queues==0,"Empty registry retained queues");
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

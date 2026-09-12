using System;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using ET.Client;
using MongoDB.Driver;

namespace ET.Server;

// Available only in the .NET host. No client protocol or production endpoint added.
[ConsoleHandler("gameplay-check")]
[FriendOf(typeof(DBComponent))]
public sealed class GameplayAcceptance : IConsoleHandler
{
    public async ETTask Run(Fiber fiber, ModeContex context, string content)
    {
        try
        {
            if (Options.Instance.Develop != 1 || Environment.GetEnvironmentVariable("ET_MONGO_DATABASE") != "ET_LOCAL_GAMEPLAY")
                throw new InvalidOperationException("Requires development mode and isolated ET_LOCAL_GAMEPLAY database");
            bool saveFailure=content.EndsWith(" save-failure",StringComparison.Ordinal);
            bool leaseExpiry=content.EndsWith(" lease-expiry",StringComparison.Ordinal);
            if(content.EndsWith(" credentials",StringComparison.Ordinal)) await Credentials(fiber.Root);
            else if(content.EndsWith(" governance",StringComparison.Ordinal)) await Governance(fiber.Root);
            else if(content.EndsWith(" baseline",StringComparison.Ordinal)) await Baseline(fiber.Root);
            else await Execute(fiber.Root, content.EndsWith(" resume",StringComparison.Ordinal), content.EndsWith(" outage",StringComparison.Ordinal) || saveFailure || leaseExpiry, saveFailure, leaseExpiry);
            Console.WriteLine("GAMEPLAY_RESULT PASS");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"GAMEPLAY_RESULT FAIL {exception.GetType().Name}: {exception.Message}");
        }
        finally { context.Parent.RemoveComponent<ModeContex>(); }
    }

    private static async ETTask Execute(Scene parent,bool resume,bool outage,bool saveFailure,bool leaseExpiry)
    {
        long id = IdGenerater.Instance.GenerateId();
        using Scene client = EntitySceneFactory.CreateScene(parent, id, id, SceneType.BenchmarkClient, "AcceptanceClient");
        client.AddComponent<TimerComponent>();
        client.AddComponent<GameplayProbe>();
        NetComponent net = client.AddComponent<NetComponent, AddressFamily, NetworkProtocol>(AddressFamily.InterNetwork, NetworkProtocol.UDP);
        IPEndPoint realm = StartSceneConfigCategory.Instance.Realms[0].InnerIPPort;
        if (!IPAddress.IsLoopback(realm.Address)) throw new InvalidOperationException("Only loopback endpoints allowed");
        string account = Environment.GetEnvironmentVariable("ET_GAMEPLAY_ACCOUNT") ?? throw new InvalidOperationException("Test account missing");
        string password = Environment.GetEnvironmentVariable("ET_GAMEPLAY_PASSWORD") ?? throw new InvalidOperationException("Test password missing");
        Stopwatch watch = Stopwatch.StartNew();

        using R2C_Login firstKey = await RealmLogin(net, realm, account, password);
        Check(firstKey.Error == 0, $"Registration/login failed: {firstKey.Error}");
        using Session first = net.Create(NetworkHelper.ToIPEndPoint(firstKey.Address));
        using G2C_LoginGate firstLogin = await GateLogin(first, firstKey);
        Check(firstLogin.Error == 0 && firstLogin.PlayerId != 0, $"First Gate login failed: {firstLogin.Error}");
        Console.WriteLine($"GAMEPLAY_ROLE {firstLogin.PlayerId}");
        if (resume)
        {
            Check(firstLogin.PlayerId == long.Parse(Environment.GetEnvironmentVariable("ET_GAMEPLAY_ROLEID")),"Server restart changed persisted role identity");
            using G2C_EnterMap restored=(G2C_EnterMap)await first.Call(C2G_EnterMap.Create(),10000);
            Check(restored.Error==0,"Restored role cannot enter map");
            await client.GetComponent<TimerComponent>().WaitAsync(1500);
            await Echo(first,201);
            Console.WriteLine("GAMEPLAY_PASS server-restart-role-reload-and-map-rpc");
            return;
        }
        Console.WriteLine("GAMEPLAY_PASS account-registration-and-gate-login");

        using Session replay = net.Create(NetworkHelper.ToIPEndPoint(firstKey.Address));
        using G2C_LoginGate replayResult = await GateLogin(replay, firstKey);
        Check(replayResult.Error == ErrorCore.ERR_ConnectGateKeyError, "Replayed Gate key accepted");
        Console.WriteLine("GAMEPLAY_PASS gate-key-replay-rejected");

        using R2C_Login wrong = await RealmLogin(net, realm, account, password + "wrong");
        Check(wrong.Error == ErrorCore.ERR_InvalidAccountOrPassword, $"Wrong password result: {wrong.Error}");
        Console.WriteLine("GAMEPLAY_PASS wrong-password-rejected");

        using G2C_EnterMap enter = (G2C_EnterMap)await first.Call(C2G_EnterMap.Create(), 10000);
        Check(enter.Error == 0 && enter.MyId == firstLogin.PlayerId, $"EnterMap failed: {enter.Error}");
        await client.GetComponent<TimerComponent>().WaitAsync(1500);
        await Echo(first, 101);
        Console.WriteLine("GAMEPLAY_PASS gate-map-rpc");
        if(outage)
        {
            string directory=Environment.GetEnvironmentVariable("ET_LOCAL_MONGO_ROOT");
            long failuresBefore=Metric("et_role_save_failures_total");
            long savesBefore=Metric("et_role_save_successes_total");
            Console.WriteLine("GAMEPLAY_OUTAGE_STOP");
            await WaitForMarker(client,Path.Combine(directory,"outage-stopped"));
            if(leaseExpiry)
            {
                Stopwatch lease=Stopwatch.StartNew();
                for(int i=0;i<140 && !first.IsDisposed;i++)
                {
                    if(i%15==0)Console.WriteLine($"GAMEPLAY_OUTAGE_PROGRESS lease_seconds={i}");
                    try { await Echo(first,900+i); }
                    catch(Exception) when(first.IsDisposed) { break; }
                    await client.GetComponent<TimerComponent>().WaitAsync(1000);
                }
                Check(first.IsDisposed,"Unverifiable account session did not expire");
                Check(lease.Elapsed.TotalSeconds>=100,"Session expired before the short-outage grace period");
                Console.WriteLine($"GAMEPLAY_PASS unverifiable-session-lease-expired elapsed_ms={lease.ElapsedMilliseconds}");
                Console.WriteLine("GAMEPLAY_OUTAGE_RESTORE");
                await WaitForMarker(client,Path.Combine(directory,"outage-restored"));
                return;
            }
            for(int i=0;i<(saveFailure?35:95);i++)
            {
                await Echo(first,400+i);
                if(i%15==0)Console.WriteLine($"GAMEPLAY_OUTAGE_PROGRESS seconds={i}");
                await client.GetComponent<TimerComponent>().WaitAsync(1000);
            }
            if(saveFailure)
            {
                Console.WriteLine("GAMEPLAY_EXPECT_SAVE_FAILURE");
                ShutdownCoordinator.Instance.Request();
                return;
            }
            Check(Metric("et_role_save_failures_total")>failuresBefore,"Periodic save failure was not observed during outage");
            Console.WriteLine("GAMEPLAY_OUTAGE_RESTORE");
            await WaitForMarker(client,Path.Combine(directory,"outage-restored"));
            await Echo(first,500);
            for(int i=0;i<75 && Metric("et_role_save_successes_total")<=savesBefore;i++)
            {
                await Echo(first,600+i);
                if(i%15==0)Console.WriteLine($"GAMEPLAY_OUTAGE_PROGRESS recovery_seconds={i}");
                await client.GetComponent<TimerComponent>().WaitAsync(1000);
            }
            Check(Metric("et_role_save_successes_total")>savesBefore,"Periodic saving did not recover");
            Console.WriteLine("GAMEPLAY_PASS database-outage-map-kept-and-service-recovered");
            Console.WriteLine("GAMEPLAY_PASS periodic-save-failed-then-recovered");
            return;
        }
        using G2C_EnterMap repeatedEnter = (G2C_EnterMap)await first.Call(C2G_EnterMap.Create(),10000);
        Check(repeatedEnter.Error == ErrorCore.ERR_PlayerStateConflict, "Already-entered player can enter map twice");
        Console.WriteLine("GAMEPLAY_PASS duplicate-enter-map-rejected");

        using R2C_Login secondKey = await RealmLogin(net, realm, account, password);
        using Session second = net.Create(NetworkHelper.ToIPEndPoint(secondKey.Address));
        using G2C_LoginGate secondLogin = await GateLogin(second, secondKey);
        Check(secondLogin.Error == 0 && secondLogin.PlayerId == firstLogin.PlayerId, $"Duplicate login lost role identity: {secondLogin.Error}");
        await client.GetComponent<TimerComponent>().WaitAsync(500);
        Check(first.IsDisposed, "Previous connection not evicted");
        using G2C_EnterMap secondEnter = (G2C_EnterMap)await second.Call(C2G_EnterMap.Create(),10000);
        Check(secondEnter.Error == 0, $"Re-enterMap failed: {secondEnter.Error}");
        await client.GetComponent<TimerComponent>().WaitAsync(1500);
        await Echo(second, 102);
        Console.WriteLine("GAMEPLAY_PASS duplicate-login-old-session-eviction-and-map-reload");

        second.Dispose();
        await client.GetComponent<TimerComponent>().WaitAsync(500);
        using R2C_Login thirdKey = await RealmLogin(net, realm, account, password);
        using Session third = net.Create(NetworkHelper.ToIPEndPoint(thirdKey.Address));
        using G2C_LoginGate thirdLogin = await GateLogin(third,thirdKey);
        Check(thirdLogin.Error == 0 && thirdLogin.PlayerId == firstLogin.PlayerId, $"Reconnect lost role identity: {thirdLogin.Error}");
        using G2C_EnterMap thirdEnter = (G2C_EnterMap)await third.Call(C2G_EnterMap.Create(),10000);
        Check(thirdEnter.Error == 0, "Reconnect enter map failed");
        await client.GetComponent<TimerComponent>().WaitAsync(1500);
        await Echo(third,103);
        Console.WriteLine($"GAMEPLAY_PASS disconnect-reconnect-map-rpc elapsed_ms={watch.ElapsedMilliseconds}");
        third.Dispose();
        await RoomReconnect(client,net,realm,account,password);
        await RouterPath(parent,client,net,realm,account+"route",password);

        using R2C_Login expiringKey = await RealmLogin(net,realm,account,password);
        Check(expiringKey.Error == 0,"Cannot issue expiry test key");
        await client.GetComponent<TimerComponent>().WaitAsync(21000);
        using Session expired = net.Create(NetworkHelper.ToIPEndPoint(expiringKey.Address));
        using G2C_LoginGate expiredResult = await GateLogin(expired,expiringKey);
        Check(expiredResult.Error == ErrorCore.ERR_ConnectGateKeyError,"Expired Gate key accepted");
        Console.WriteLine("GAMEPLAY_PASS expired-gate-key-rejected");

        bool limited=false;
        for(int i=0;i<12;i++)
        {
            using R2C_Login attempt=await RealmLogin(net,realm,account,password);
            if(attempt.Error==ErrorCore.ERR_LoginRateLimited){limited=true;break;}
            Check(attempt.Error==0,$"Unexpected rate-limit test error: {attempt.Error}");
        }
        Check(limited,"Real network login rate limit not enforced");
        Console.WriteLine("GAMEPLAY_PASS login-rate-limit-enforced");

        using Session flood=net.Create(NetworkHelper.ToIPEndPoint(firstKey.Address));
        await client.GetComponent<TimerComponent>().WaitAsync(300);
        for(int i=0;i<Options.Instance.OuterMessageLimit+20;i++)
        {
            C2G_LoginGate invalid=C2G_LoginGate.Create();invalid.Key=0;flood.Send(invalid);
        }
        await client.GetComponent<TimerComponent>().WaitAsync(1500);
        Check(flood.IsDisposed,"Message flood connection was not closed");
        Console.WriteLine("GAMEPLAY_PASS message-flood-disconnected");
    }

    private static async ETTask<R2C_Login> RealmLogin(NetComponent net, IPEndPoint address, string account, string password)
    {
        using Session session = net.Create(address);
        C2R_Login request = C2R_Login.Create(); request.Account=account; request.Password=password;
        return (R2C_Login)await session.Call(request,10000);
    }

    private static async ETTask<G2C_LoginGate> GateLogin(Session session, R2C_Login key)
    {
        C2G_LoginGate request=C2G_LoginGate.Create();request.Key=key.Key;request.GateId=key.GateId;
        return (G2C_LoginGate)await session.Call(request,10000);
    }

    private static async ETTask Echo(Session session,int number)
    {
        C2M_TestRobotCase request=C2M_TestRobotCase.Create();request.N=number;
        using M2C_TestRobotCase response=(M2C_TestRobotCase)await session.Call(request,10000);
        Check(response.Error==0 && response.N==number,$"Map echo failed: {response.Error}");
    }

    private static async ETTask RoomReconnect(Scene client,NetComponent net,IPEndPoint realm,string account,string password)
    {
        List<Session> members = new();
        GameplayProbe probe=client.GetComponent<GameplayProbe>();
        long firstId=0;
        try
        {
            for(int i=0;i<LSConstValue.MatchCount;i++)
            {
                using R2C_Login key=await RealmLogin(net,realm,account+"r"+i,password);
                Check(key.Error==0,"Room account login failed");
                Session member=net.Create(NetworkHelper.ToIPEndPoint(key.Address));members.Add(member);
                using G2C_LoginGate login=await GateLogin(member,key);
                Check(login.Error==0,"Room Gate login failed");
                if(i==0)firstId=login.PlayerId;
                using G2C_Match match=(G2C_Match)await member.Call(C2G_Match.Create(),10000);
                Check(match.Error==0,"Match request failed");
            }
            for(int i=0;i<40 && probe.Matches<LSConstValue.MatchCount;i++)await client.GetComponent<TimerComponent>().WaitAsync(100);
            Check(probe.Matches==LSConstValue.MatchCount,"Match notifications missing");
            foreach(Session member in members)member.Send(C2Room_ChangeSceneFinish.Create());
            for(int i=0;i<40 && probe.Starts<LSConstValue.MatchCount;i++)await client.GetComponent<TimerComponent>().WaitAsync(100);
            Check(probe.Starts==LSConstValue.MatchCount,"Room start notifications missing");
            members[0].Dispose();
            await client.GetComponent<TimerComponent>().WaitAsync(300);
            using R2C_Login reconnectKey=await RealmLogin(net,realm,account+"r0",password);
            using Session reconnectSession=net.Create(NetworkHelper.ToIPEndPoint(reconnectKey.Address));
            using G2C_LoginGate loginAgain=await GateLogin(reconnectSession,reconnectKey);
            Check(loginAgain.Error==0 && loginAgain.PlayerId==firstId,"Room reconnect changed player identity");
            for(int i=0;i<30 && probe.Reconnects==0;i++)await client.GetComponent<TimerComponent>().WaitAsync(100);
            Check(probe.Reconnects==1 && probe.ReconnectUnits==LSConstValue.MatchCount && probe.ReconnectStartTime>0,"Room reconnect snapshot missing or invalid");
            Console.WriteLine("GAMEPLAY_PASS room-match-start-and-reconnect-snapshot");
        }
        finally {foreach(Session member in members)member.Dispose();}
    }

    private static async ETTask RouterPath(Scene parent,Scene client,NetComponent net,IPEndPoint realm,string account,string password)
    {
        RouterAddressComponent addresses=parent.AddComponent<RouterAddressComponent,string,int>(ConstValue.RouterHttpHost,ConstValue.RouterHttpPort);
        try
        {
            await addresses.Init();
            using Session realmSession=await net.CreateRouterSession(realm,account,password);
            C2R_Login request=C2R_Login.Create();request.Account=account;request.Password=password;
            using R2C_Login key=(R2C_Login)await realmSession.Call(request,10000);
            Check(key.Error==0,"Router Realm authentication failed");
            using Session gateSession=await net.CreateRouterSession(NetworkHelper.ToIPEndPoint(key.Address),account,password);
            using G2C_LoginGate login=await GateLogin(gateSession,key);
            Check(login.Error==0,"Router Gate authentication failed");
            using G2C_EnterMap enter=(G2C_EnterMap)await gateSession.Call(C2G_EnterMap.Create(),10000);
            Check(enter.Error==0,"Router enter map failed");
            await client.GetComponent<TimerComponent>().WaitAsync(1500);
            await Echo(gateSession,301);
            Console.WriteLine("GAMEPLAY_PASS router-realm-gate-map-chain");
        }
        finally {parent.RemoveComponent<RouterAddressComponent>();}
    }

    private static void Check(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    private static long Metric(string name)
    {
        foreach(string line in OperationalMetrics.Instance.ExportPrometheus().Split('\n'))
            if(line.StartsWith(name+" ",StringComparison.Ordinal))return long.Parse(line.Substring(name.Length+1));
        throw new InvalidOperationException("Metric missing");
    }

    private static async ETTask WaitForMarker(Scene scene,string path)
    {
        for(int i=0;i<60;i++)
        {
            if(File.Exists(path))return;
            await scene.GetComponent<TimerComponent>().WaitAsync(1000);
        }
        throw new TimeoutException("Database fault coordinator did not respond");
    }

    private static async ETTask Baseline(Scene parent)
    {
        long id=IdGenerater.Instance.GenerateId();
        using Scene client=EntitySceneFactory.CreateScene(parent,id,id,SceneType.BenchmarkClient,"AcceptanceLoad");
        NetComponent net=client.AddComponent<NetComponent,AddressFamily,NetworkProtocol>(AddressFamily.InterNetwork,NetworkProtocol.UDP);
        IPEndPoint realm=StartSceneConfigCategory.Instance.Realms[0].InnerIPPort;
        Check(IPAddress.IsLoopback(realm.Address), "Only loopback endpoints allowed");
        string prefix=Environment.GetEnvironmentVariable("ET_GAMEPLAY_ACCOUNT");
        string password=Environment.GetEnvironmentVariable("ET_GAMEPLAY_PASSWORD");
        List<Session> sessions=new();List<string> errors=new();List<ETTask> tasks=new();
        int count=ReadLoadSetting("ET_GAMEPLAY_CLIENTS",20,1,3000);
        int duration=ReadLoadSetting("ET_GAMEPLAY_DURATION",0,0,86400);
        int batch=Math.Min(20,Options.Instance.LoginIpLimit);
        Check(batch>0,"Login IP limit must be positive");
        long[] latency=new long[count];Stopwatch total=Stopwatch.StartNew();
        using Process process=Process.GetCurrentProcess();
        TimeSpan cpuBefore=process.TotalProcessorTime;
        long peakWorkingSet=0;
        long requests=0;
        long maxRpcMs=0;
        long savesBefore=Metric("et_role_save_successes_total");
        long failuresBefore=Metric("et_role_save_failures_total");
        try
        {
            for(int offset=0;offset<count;offset+=batch)
            {
                tasks.Clear();
                for(int i=offset;i<Math.Min(offset+batch,count);i++)tasks.Add(Connect(i));
                await ETTaskHelper.WaitAll(tasks);
                Check(errors.Count==0,$"Concurrent clients failed: {string.Join(';',errors)}");
                Console.WriteLine($"GAMEPLAY_PROGRESS connected={sessions.Count} target={count}");
                if(sessions.Count<count)
                {
                    // Keep the production anti-abuse limit; a single loopback source must ramp slowly.
                    for(int seconds=0;seconds<Options.Instance.LoginRateWindowSeconds+1;seconds+=5)
                    {
                        await Probe();
                        Console.WriteLine($"GAMEPLAY_PROGRESS ramp_wait_seconds={seconds}");
                        await parent.GetComponent<TimerComponent>().WaitAsync(5000);
                    }
                }
            }
            Check(errors.Count==0,$"Concurrent clients failed: {string.Join(';',errors)}");
            Check(sessions.Count==count,"Concurrent connection count mismatch");
            Stopwatch sustained=Stopwatch.StartNew();
            do
            {
                await Probe();
                process.Refresh();
                peakWorkingSet=Math.Max(peakWorkingSet,process.WorkingSet64);
                Console.WriteLine($"GAMEPLAY_PROGRESS online={sessions.Count} sustained_ms={sustained.ElapsedMilliseconds} rpc_count={requests} rpc_max_ms={maxRpcMs} working_set_bytes={process.WorkingSet64}");
                if(sustained.Elapsed.TotalSeconds>=duration)break;
                await parent.GetComponent<TimerComponent>().WaitAsync(5000);
            }while(true);
            Array.Sort(latency);
            long saveFailures=Metric("et_role_save_failures_total")-failuresBefore;
            Check(saveFailures==0,"Role saving failed during load test");
            Console.WriteLine($"GAMEPLAY_PASS concurrent-clients count={count} errors=0 flow_p95_ms={latency[(int)Math.Ceiling(count*0.95)-1]} total_ms={total.ElapsedMilliseconds} sustained_ms={sustained.ElapsedMilliseconds} rpc_count={requests} rpc_max_ms={maxRpcMs} peak_sampled_working_set_bytes={peakWorkingSet} cpu_ms={(long)(process.TotalProcessorTime-cpuBefore).TotalMilliseconds} role_saves={Metric("et_role_save_successes_total")-savesBefore} role_save_failures={saveFailures}");
        }
        finally{foreach(Session session in sessions)session.Dispose();}

        async ETTask Probe()
        {
            foreach(Session session in sessions)
            {
                Check(!session.IsDisposed,"Concurrent connection dropped");
                Stopwatch request=Stopwatch.StartNew();
                await Echo(session,701);
                maxRpcMs=Math.Max(maxRpcMs,request.ElapsedMilliseconds);
                ++requests;
                if(requests%100==0)Console.WriteLine($"GAMEPLAY_PROGRESS rpc_count={requests} rpc_max_ms={maxRpcMs}");
            }
        }

        async ETTask Connect(int index)
        {
            Stopwatch clock=Stopwatch.StartNew();
            try
            {
                using R2C_Login key=await RealmLogin(net,realm,prefix+"p"+index,password);
                Check(key.Error==0,$"Load Realm error {key.Error}");
                Session session=net.Create(NetworkHelper.ToIPEndPoint(key.Address));sessions.Add(session);
                using G2C_LoginGate login=await GateLogin(session,key);Check(login.Error==0,$"Load Gate error {login.Error}");
                using G2C_EnterMap enter=(G2C_EnterMap)await session.Call(C2G_EnterMap.Create(),10000);Check(enter.Error==0,"Load Map error");
                await parent.GetComponent<TimerComponent>().WaitAsync(1500);await Echo(session,index);
                latency[index]=clock.ElapsedMilliseconds;
            }
            catch(Exception error){errors.Add(error.Message);}
        }
    }

    private static int ReadLoadSetting(string name,int fallback,int minimum,int maximum)
    {
        string value=Environment.GetEnvironmentVariable(name);
        if(string.IsNullOrEmpty(value))return fallback;
        if(!int.TryParse(value,out int parsed)||parsed<minimum||parsed>maximum)
            throw new InvalidOperationException($"Invalid load setting: {name}");
        return parsed;
    }

    private static async ETTask Credentials(Scene parent)
    {
        long id=IdGenerater.Instance.GenerateId();
        using Scene client=EntitySceneFactory.CreateScene(parent,id,id,SceneType.BenchmarkClient,"CredentialTest");
        var net=client.AddComponent<NetComponent,AddressFamily,NetworkProtocol>(AddressFamily.InterNetwork,NetworkProtocol.UDP);
        var realm=StartSceneConfigCategory.Instance.Realms[0].InnerIPPort;
        Check(IPAddress.IsLoopback(realm.Address),"Only loopback endpoints allowed");
        string name=Environment.GetEnvironmentVariable("ET_GAMEPLAY_ACCOUNT")+"cred";
        string oldPassword=Environment.GetEnvironmentVariable("ET_GAMEPLAY_PASSWORD"),newPassword=oldPassword+"new";
        var db=client.AddComponent<DBManagerComponent>().GetZoneDB(1);
        var accounts=db.database.GetCollection<Account>(typeof(Account).FullName);
        using R2C_Login initial=await RealmLogin(net,realm,name,oldPassword);
        Check(initial.Error==0,"Credential fixture registration failed");
        using Session oldSession=net.Create(NetworkHelper.ToIPEndPoint(initial.Address));
        using G2C_LoginGate oldGate=await GateLogin(oldSession,initial);
        Check(oldGate.Error==0,"Credential fixture Gate failed");
        using R2C_Login pending=await RealmLogin(net,realm,name,oldPassword);
        Check(pending.Error==0,"Pending key failed");
        var account=await accounts.Find(a=>a.LoginName==name).SingleAsync();
        using var rsa=System.Security.Cryptography.RSA.Create(4096);
        string encrypted=Convert.ToBase64String(rsa.Encrypt(System.Text.Encoding.UTF8.GetBytes(newPassword),System.Security.Cryptography.RSAEncryptionPadding.OaepSHA256));
        var hash=AccountPasswordEnvelope.DecryptAndHash(encrypted,rsa.ExportPkcs8PrivateKeyPem());
        string digest=AccountPasswordEnvelope.Digest(encrypted),operation=Guid.NewGuid().ToString("N");
        bool injected=false;
        try {await AccountStateStore.Execute(db,account.Id,account.StateVersion,"reset-password","local-test","test-reset",Guid.NewGuid().ToString("N"),
            ()=>{injected=true;throw new InvalidOperationException("Injected credential rollback");},hash,digest);}
        catch(InvalidOperationException){Check(injected,"Credential failure occurred before injection");}
        Check(injected,"Credential fault injection missing");
        using R2C_Login unchanged=await RealmLogin(net,realm,name,oldPassword);
        Check(unchanged.Error==0,"Aborted reset changed password");
        Check(await AccountStateStore.Execute(db,account.Id,account.StateVersion,"reset-password","local-test","test-reset",operation,passwordHash:hash,credentialDigest:digest),"Reset failed");
        Check(!await AccountStateStore.Execute(db,account.Id,account.StateVersion,"reset-password","local-test","test-reset",operation,passwordHash:hash,credentialDigest:digest),"Reset replay repeated mutation");
        bool conflict=false;
        try {await AccountStateStore.Execute(db,account.Id,account.StateVersion,"reset-password","local-test","test-reset",operation,passwordHash:hash,credentialDigest:"different");}
        catch(InvalidOperationException){conflict=true;}
        Check(conflict,"Operation id accepted another credential envelope");
        using Session stale=net.Create(NetworkHelper.ToIPEndPoint(pending.Address));
        using G2C_LoginGate staleKey=await GateLogin(stale,pending);
        Check(staleKey.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Pre-reset key remained valid");
        for(int i=0;i<100 && !oldSession.IsDisposed;i++)await parent.GetComponent<TimerComponent>().WaitAsync(100);
        Check(oldSession.IsDisposed,"Reset retained old session");
        using R2C_Login oldRejected=await RealmLogin(net,realm,name,oldPassword);
        using R2C_Login fresh=await RealmLogin(net,realm,name,newPassword);
        Check(oldRejected.Error==ErrorCore.ERR_InvalidAccountOrPassword && fresh.Error==0,"Password replacement authentication failed");
        using Session freshSession=net.Create(NetworkHelper.ToIPEndPoint(fresh.Address));
        using G2C_LoginGate freshGate=await GateLogin(freshSession,fresh);
        Check(freshGate.Error==0 && freshGate.PlayerId==oldGate.PlayerId,"Reset lost role identity");
        var saved=await accounts.Find(a=>a.Id==account.Id).SingleAsync();
        Check(saved.StateVersion==account.StateVersion+1 && saved.Status==AccountStatus.Active,"Reset state/version incorrect");
        var audit=await db.database.GetCollection<MongoDB.Bson.BsonDocument>(AccountStateStore.AuditCollection).Find(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id",operation)).SingleAsync();
        Check(!audit.Contains("PasswordHash")&&!audit.Contains("PasswordSalt")&&!audit.Contains("EncryptedPassword")&&audit["CredentialDigest"].AsString==digest,"Credential audit contains sensitive payload");
        Console.WriteLine("GAMEPLAY_PASS encrypted-password-reset-rollback-idempotency-old-credentials-revoked");
    }

    private static async ETTask Governance(Scene parent)
    {
        long id=IdGenerater.Instance.GenerateId();
        using Scene client=EntitySceneFactory.CreateScene(parent,id,id,SceneType.BenchmarkClient,"AccountGovernanceTest");
        NetComponent net=client.AddComponent<NetComponent,AddressFamily,NetworkProtocol>(AddressFamily.InterNetwork,NetworkProtocol.UDP);
        IPEndPoint realm=StartSceneConfigCategory.Instance.Realms[0].InnerIPPort;
        Check(IPAddress.IsLoopback(realm.Address),"Only loopback endpoints allowed");
        string name=Environment.GetEnvironmentVariable("ET_GAMEPLAY_ACCOUNT")+"gov";
        string password=Environment.GetEnvironmentVariable("ET_GAMEPLAY_PASSWORD");
        DBComponent db=client.AddComponent<DBManagerComponent>().GetZoneDB(1);
        var accounts=db.database.GetCollection<Account>(typeof(Account).FullName);
        using R2C_Login key=await RealmLogin(net,realm,name,password);
        Check(key.Error==0,"Governance registration failed");
        // Simulate an account created before StateVersion existed.
        await accounts.UpdateOneAsync(a=>a.LoginName==name,Builders<Account>.Update.Unset(a=>a.StateVersion));
        try
        {
            var disabled=await accounts.UpdateOneAsync(a=>a.LoginName==name,Builders<Account>.Update.Set(a=>a.Status,AccountStatus.Disabled));
            Check(disabled.MatchedCount==1,"Test account missing");
            using Session rejected=net.Create(NetworkHelper.ToIPEndPoint(key.Address));
            using G2C_LoginGate gate=await GateLogin(rejected,key);
            Check(gate.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Issued key bypassed disabled status");
            using R2C_Login realmRejected=await RealmLogin(net,realm,name,password);
            Check(realmRejected.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Disabled account authenticated");
            Console.WriteLine("GAMEPLAY_PASS disabled-account-realm-and-issued-gate-key-rejected");
        }
        finally
        {
            await accounts.UpdateOneAsync(a=>a.LoginName==name,Builders<Account>.Update.Set(a=>a.Status,AccountStatus.Active));
        }
        using Session replay=net.Create(NetworkHelper.ToIPEndPoint(key.Address));
        using G2C_LoginGate consumed=await GateLogin(replay,key);
        Check(consumed.Error==ErrorCore.ERR_ConnectGateKeyError,"Rejected key became reusable after enable");
        using R2C_Login fresh=await RealmLogin(net,realm,name,password);
        Check(fresh.Error==0,"Re-enabled account failed");
        using Session session=net.Create(NetworkHelper.ToIPEndPoint(fresh.Address));
        using G2C_LoginGate login=await GateLogin(session,fresh);
        Check(login.Error==0,"Role creation failed");
        using R2C_Login again=await RealmLogin(net,realm,name,password);
        Check(again.Error==0,"Subsequent authentication failed");
        var stored=await accounts.Find(a=>a.LoginName==name).SingleAsync();
        Check(stored.DefaultRoleId==login.PlayerId && stored.Status==AccountStatus.Active,"Authentication overwrote role association");
        Console.WriteLine("GAMEPLAY_PASS reenabled-fresh-login-and-role-link-preserved");
        using G2C_EnterMap initialMap=(G2C_EnterMap)await session.Call(C2G_EnterMap.Create(),10000);
        Check(initialMap.Error==0,"Governance map entry failed");
        await parent.GetComponent<TimerComponent>().WaitAsync(1500);
        await Echo(session,801);
        await AuditTransactions(db,stored);
        using Session staleKeySession=net.Create(NetworkHelper.ToIPEndPoint(again.Address));
        using G2C_LoginGate staleKeyResult=await GateLogin(staleKeySession,again);
        Check(staleKeyResult.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Unconsumed key survived disable-enable version changes");
        Console.WriteLine("GAMEPLAY_PASS unconsumed-key-revoked-by-account-version");
        for(int i=0;i<100 && !session.IsDisposed;i++)await parent.GetComponent<TimerComponent>().WaitAsync(100);
        Check(session.IsDisposed,"Old session survived account state version changes");
        Console.WriteLine("GAMEPLAY_PASS old-session-revoked-after-disable-enable");
        using R2C_Login newKey=await RealmLogin(net,realm,name,password);
        Check(newKey.Error==0,"Post-revocation authentication failed");
        using Session newSession=net.Create(NetworkHelper.ToIPEndPoint(newKey.Address));
        using G2C_LoginGate newLogin=await GateLogin(newSession,newKey);
        Check(newLogin.Error==0 && newLogin.PlayerId==login.PlayerId,"Post-revocation role identity lost");
        using G2C_EnterMap newMap=(G2C_EnterMap)await newSession.Call(C2G_EnterMap.Create(),10000);
        Check(newMap.Error==0,"Post-revocation map entry failed");
        await parent.GetComponent<TimerComponent>().WaitAsync(6500);
        Check(!newSession.IsDisposed,"Current account version session was incorrectly revoked");
        await Echo(newSession,802);
        Console.WriteLine("GAMEPLAY_PASS fresh-session-retained-and-map-restored");
        var active=await accounts.Find(a=>a.Id==stored.Id).SingleAsync();
        await AccountStateStore.Change(db,active.Id,active.StateVersion,AccountStatus.Disabled,"local-test","test-online-disable",Guid.NewGuid().ToString("N"));
        try
        {
            for(int i=0;i<100 && !newSession.IsDisposed;i++)await parent.GetComponent<TimerComponent>().WaitAsync(100);
            Check(newSession.IsDisposed,"Disabled online account remained connected");
            Console.WriteLine("GAMEPLAY_PASS disabled-online-session-disconnected");
        }
        finally
        {
            await AccountStateStore.Change(db,active.Id,active.StateVersion+1,AccountStatus.Active,"local-test","test-enable",Guid.NewGuid().ToString("N"));
        }
        active=await accounts.Find(a=>a.Id==stored.Id).SingleAsync();
        using R2C_Login revokeKey=await RealmLogin(net,realm,name,password);
        Check(revokeKey.Error==0,"Pre-revoke authentication failed");
        using Session revoked=net.Create(NetworkHelper.ToIPEndPoint(revokeKey.Address));
        using G2C_LoginGate beforeRevoke=await GateLogin(revoked,revokeKey);
        Check(beforeRevoke.Error==0,"Pre-revoke gate failed");
        await AccountStateStore.Execute(db,active.Id,active.StateVersion,"revoke","local-test","test-revoke",Guid.NewGuid().ToString("N"));
        for(int i=0;i<100 && !revoked.IsDisposed;i++)await parent.GetComponent<TimerComponent>().WaitAsync(100);
        Check(revoked.IsDisposed,"Explicit revoke left old connection alive");
        using R2C_Login closeKey=await RealmLogin(net,realm,name,password);
        Check(closeKey.Error==0,"Explicit revoke prevented fresh authentication");
        var beforeClose=await accounts.Find(a=>a.Id==active.Id).SingleAsync();
        Check(beforeClose.Status==AccountStatus.Active,"Explicit revoke changed active status");
        bool rollback=false;
        try {await AccountStateStore.Execute(db,active.Id,beforeClose.StateVersion,"close","local-test","test-close",Guid.NewGuid().ToString("N"),()=>throw new InvalidOperationException("Injected close failure"));}
        catch(InvalidOperationException){rollback=true;}
        Check(rollback,"Close fault injection did not run");
        var rolledBack=await accounts.Find(a=>a.Id==active.Id).SingleAsync();
        Check(rolledBack.Status==AccountStatus.Active && rolledBack.ClosedAt==0 && rolledBack.StateVersion==beforeClose.StateVersion,"Close rollback retained partial state");
        string closeOperation=Guid.NewGuid().ToString("N");
        Check(await AccountStateStore.Execute(db,active.Id,beforeClose.StateVersion,"close","local-test","test-close",closeOperation),"Close did not commit");
        Check(!await AccountStateStore.Execute(db,active.Id,beforeClose.StateVersion,"close","local-test","test-close",closeOperation),"Close replay applied twice");
        using Session closedSession=net.Create(NetworkHelper.ToIPEndPoint(closeKey.Address));
        using G2C_LoginGate closedGate=await GateLogin(closedSession,closeKey);
        Check(closedGate.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Closed account used prior gate key");
        using R2C_Login closedRealm=await RealmLogin(net,realm,name,password);
        Check(closedRealm.Error==ErrorCore.ERR_InvalidAccountOrPassword,"Closed account authenticated or auto-registered");
        var closed=await accounts.Find(a=>a.Id==active.Id).SingleAsync();
        Check(closed.Status==AccountStatus.Closed && closed.ClosedAt>0 && closed.DefaultRoleId==stored.DefaultRoleId,"Soft close lost data or timestamp");
        bool reopenRejected=false;
        try {await AccountStateStore.Execute(db,closed.Id,closed.StateVersion,"enable","local-test","test-reopen",Guid.NewGuid().ToString("N"));}
        catch(AccountStateConflictException){reopenRejected=true;}
        Check(reopenRejected,"Enable reopened closed account");
        Console.WriteLine("GAMEPLAY_PASS explicit-revoke-and-soft-close-login-denial-rollback-idempotency");
    }

    private static async ETTask AuditTransactions(DBComponent db,Account account)
    {
        var accounts=db.database.GetCollection<Account>(typeof(Account).FullName);
        var audits=db.database.GetCollection<MongoDB.Bson.BsonDocument>(AccountStateStore.AuditCollection);
        string operation=Guid.NewGuid().ToString("N");
        Check(await AccountStateStore.Change(db,account.Id,account.StateVersion,AccountStatus.Disabled,"local-test","test-disable",operation),"State transaction did not commit");
        try
        {
            Check(!await AccountStateStore.Change(db,account.Id,account.StateVersion,AccountStatus.Disabled,"local-test","test-disable",operation),"Repeated operation applied twice");
            bool conflict=false;
            try { await AccountStateStore.Change(db,account.Id,account.StateVersion,AccountStatus.Active,"local-test","test-disable",operation); }
            catch(InvalidOperationException){conflict=true;}
            Check(conflict,"Conflicting operation id accepted");
            bool stale=false;
            try { await AccountStateStore.Change(db,account.Id,account.StateVersion,AccountStatus.Active,"local-test","test-enable",Guid.NewGuid().ToString("N")); }
            catch(InvalidOperationException){stale=true;}
            Check(stale,"Stale account version accepted");

            string failedOperation=Guid.NewGuid().ToString("N");
            bool failed=false;
            try { await AccountStateStore.Change(db,account.Id,account.StateVersion+1,AccountStatus.Active,"local-test","test-rollback",failedOperation,
                () => throw new InvalidOperationException("Injected audit failure")); }
            catch(InvalidOperationException){failed=true;}
            Check(failed,"Fault injection did not abort");
            var after=await accounts.Find(a=>a.Id==account.Id).SingleAsync();
            Check(after.Status==AccountStatus.Disabled && after.StateVersion==account.StateVersion+1,"Aborted transaction changed account");
            Check(await audits.CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id",failedOperation))==0,"Aborted transaction left audit");
            Check(await audits.CountDocumentsAsync(Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("_id",operation))==1,"Audit idempotency failed");
            Console.WriteLine("GAMEPLAY_PASS account-state-audit-atomicity-idempotency-and-version-conflicts");
        }
        finally
        {
            await AccountStateStore.Change(db,account.Id,account.StateVersion+1,AccountStatus.Active,"local-test","test-enable",Guid.NewGuid().ToString("N"));
        }
        var restored=await accounts.Find(a=>a.Id==account.Id).SingleAsync();
        Check(restored.Status==AccountStatus.Active && restored.StateVersion==account.StateVersion+2,"Audited restoration failed");
        int committed=0,conflicted=0;
        try
        {
            await ETTaskHelper.WaitAll(new List<ETTask> { Compete(), Compete() });
            Check(committed==1 && conflicted==1,"Concurrent state changes did not select exactly one winner");
            var final=await accounts.Find(a=>a.Id==account.Id).SingleAsync();
            Check(final.StateVersion==account.StateVersion+3 && final.Status==AccountStatus.Disabled,"Concurrent state version invalid");
            Console.WriteLine("GAMEPLAY_PASS concurrent-state-transactions-one-winner");
        }
        finally
        {
            var current=await accounts.Find(a=>a.Id==account.Id).SingleAsync();
            if(current.Status==AccountStatus.Disabled)
                await AccountStateStore.Change(db,account.Id,current.StateVersion,AccountStatus.Active,"local-test","test-enable",Guid.NewGuid().ToString("N"));
        }

        async ETTask Compete()
        {
            try
            {
                if(await AccountStateStore.Change(db,account.Id,account.StateVersion+2,AccountStatus.Disabled,"local-test","test-compete",Guid.NewGuid().ToString("N")))++committed;
            }
            catch(InvalidOperationException){++conflicted;}
        }
    }
}

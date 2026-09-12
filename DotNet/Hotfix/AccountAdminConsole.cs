using System;
using System.IO;

namespace ET.Server;

[ConsoleHandler("account-admin")]
public sealed class AccountAdminConsole : IConsoleHandler
{
    public async ETTask Run(Fiber fiber,ModeContex context,string content)
    {
        Scene scope=null;
        DBComponent db=null;
        AccountAdminCommand command=null;
        string requestId=Guid.NewGuid().ToString("N");
        string stage="configuration";
        bool executionStarted=false;
        try
        {
            if(!StartupCoordinator.Instance.IsReady||ShutdownCoordinator.Instance.IsRequested)
                throw new InvalidOperationException("Administration unavailable during startup or shutdown");
            string trustPath=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_TRUST_FILE");
            if(string.IsNullOrWhiteSpace(trustPath))throw new InvalidOperationException("Administration disabled");
            string uri=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_MONGO_URI");
            string database=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DATABASE");
            if(string.IsNullOrWhiteSpace(uri)||string.IsNullOrWhiteSpace(database)||
                !int.TryParse(Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_ZONE"),out int zone)||zone<=0)
                throw new InvalidOperationException("Dedicated administration database configuration required");
            var mongo=MongoDB.Driver.MongoClientSettings.FromConnectionString(uri);
            if(!mongo.UseTls||mongo.Credential==null||string.IsNullOrWhiteSpace(mongo.ReplicaSetName))
                throw new InvalidOperationException("Administration requires authenticated TLS replica set");
            long id=IdGenerater.Instance.GenerateId();
            scope=EntitySceneFactory.CreateScene(fiber.Root,id,id,SceneType.Main,"AccountAdministration");
            db=scope.AddComponent<DBManagerComponent>().AddChildWithId<DBComponent,string,string>(zone,uri,database);
            await AccountAdminAttemptStore.Append(db,requestId,"received",stage,null);
            stage="rate_limit";
            if(!AccountAdminRateLimiter.ProcessLimiter.TryAcquire(Environment.TickCount64))throw new InvalidOperationException("Administration rate limited");
            stage="authorization";
            string path=content.Substring("account-admin".Length).Trim();
            command=AccountAdminAuthorization.Verify(ReadBounded(path,32768),ReadBounded(trustPath,131072),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if(command.DeploymentId!=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DEPLOYMENT_ID"))
                throw new InvalidOperationException("Wrong deployment");
            if(command.Zone!=zone||command.Database!=database)throw new InvalidOperationException("Wrong administration scope");
            stage="execution";
            await AccountAdminAttemptStore.Append(db,requestId,"authorized",stage,command);
            executionStarted=true;
            bool applied=await AccountStateStore.Change(db,command.AccountId,command.ExpectedVersion,
                command.Action=="disable"?AccountStatus.Disabled:AccountStatus.Active,command.OperatorId,command.ReasonCode,command.OperationId);
            await AccountAdminAttemptStore.Append(db,requestId,applied?"applied":"already_applied",stage,command);
            Console.WriteLine($"ACCOUNT_ADMIN_RESULT {(applied?"APPLIED":"ALREADY_APPLIED")} operation_id={command.OperationId}");
        }
        catch(Exception)
        {
            if(db!=null)
            {
                try { await AccountAdminAttemptStore.Append(db,requestId,executionStarted?"uncertain":"rejected",stage,command); }
                catch(Exception) { Log.Warning("event=account_admin_attempt_audit_unavailable"); }
            }
            // Never print the envelope, file content, signature or connection settings.
            Console.WriteLine($"ACCOUNT_ADMIN_RESULT {(executionStarted?"UNCERTAIN":"REJECTED")} request_id={requestId}");
            Log.Warning("event=account_admin_rejected");
        }
        finally { scope?.Dispose();context.Parent.RemoveComponent<ModeContex>(); }
    }

    private static string ReadBounded(string path,int limit)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>limit)throw new InvalidOperationException("Administration file too large");
        using var reader=new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

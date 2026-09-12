using System;
using System.IO;

namespace ET.Server;

[ConsoleHandler("account-admin")]
public sealed class AccountAdminConsole : IConsoleHandler
{
    public async ETTask Run(Fiber fiber,ModeContex context,string content)
    {
        try
        {
            if(!StartupCoordinator.Instance.IsReady||ShutdownCoordinator.Instance.IsRequested)
                throw new InvalidOperationException("Administration unavailable during startup or shutdown");
            string trustPath=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_TRUST_FILE");
            if(string.IsNullOrWhiteSpace(trustPath))throw new InvalidOperationException("Administration disabled");
            string path=content.Substring("account-admin".Length).Trim();
            var command=AccountAdminAuthorization.Verify(ReadBounded(path,32768),ReadBounded(trustPath,131072),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if(command.DeploymentId!=Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DEPLOYMENT_ID"))
                throw new InvalidOperationException("Wrong deployment");
            var settings=DatabaseConnectionResolver.Resolve(command.Zone);
            if(settings.DatabaseName!=command.Database)throw new InvalidOperationException("Wrong target database");
            long id=IdGenerater.Instance.GenerateId();
            using Scene scope=EntitySceneFactory.CreateScene(fiber.Root,id,id,SceneType.Main,"AccountAdministration");
            DBComponent db=scope.AddComponent<DBManagerComponent>().GetZoneDB(command.Zone);
            bool applied=await AccountStateStore.Change(db,command.AccountId,command.ExpectedVersion,
                command.Action=="disable"?AccountStatus.Disabled:AccountStatus.Active,command.OperatorId,command.ReasonCode,command.OperationId);
            Console.WriteLine($"ACCOUNT_ADMIN_RESULT {(applied?"APPLIED":"ALREADY_APPLIED")} operation_id={command.OperationId}");
        }
        catch(Exception)
        {
            // Never print the envelope, file content, signature or connection settings.
            Console.WriteLine("ACCOUNT_ADMIN_RESULT REJECTED");
            Log.Warning("event=account_admin_rejected");
        }
        finally { context.Parent.RemoveComponent<ModeContex>(); }
    }

    private static string ReadBounded(string path,int limit)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length>limit)throw new InvalidOperationException("Administration file too large");
        using var reader=new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

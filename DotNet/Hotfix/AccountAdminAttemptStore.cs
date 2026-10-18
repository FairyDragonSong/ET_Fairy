using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
internal static class AccountAdminAttemptStore
{
    // 追加一条不可缺失的审计记录。
    internal static async ETTask Append(DBComponent database,string requestId,string outcome,string stage,AccountAdminCommand command)
    {
        var record=new BsonDocument
        {
            {"_id",requestId+":"+outcome},{"RequestId",requestId},{"Outcome",outcome},{"Stage",stage},
            {"CreatedAtUtc",DateTime.UtcNow},{"Authenticated",command!=null}
        };
        record.Add("DeploymentId",Environment.GetEnvironmentVariable("ET_ACCOUNT_ADMIN_DEPLOYMENT_ID")??"unconfigured");
        if(command!=null)
        {
            record.Add("OperatorId",command.OperatorId);
            record.Add("OperationId",command.OperationId);
            record.Add("AccountId",command.AccountId);
        }
        await database.database.GetCollection<BsonDocument>("ET.Server.AccountAdminAttempt")
            .WithWriteConcern(WriteConcern.WMajority).InsertOneAsync(record);
    }
}

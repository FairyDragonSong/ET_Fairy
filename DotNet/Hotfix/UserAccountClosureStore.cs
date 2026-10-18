using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
internal static class UserAccountClosureStore
{
    // 在版本匹配时软注销账号并写入审计。
    internal static async ETTask<bool> Close(DBComponent db,SelfAccountClosure request,long expectedVersion,string digest,
        Action beforeAudit=null,Func<System.Threading.Tasks.Task> beforeUpdate=null)
    {
        if(request==null||expectedVersion<0||expectedVersion==long.MaxValue||!Guid.TryParseExact(request.OperationId,"N",out _)||
            !AccountInputValidator.TryNormalizeLoginName(request.LoginName,out string normalized)||normalized!=request.LoginName||
            request.ConfirmLoginName!=request.LoginName||!AccountInputValidator.IsPasswordValid(request.Password)||
            string.IsNullOrEmpty(digest)||digest.Length!=64)
            throw new AccountStateConflictException();
        var accounts=db.database.GetCollection<Account>(typeof(Account).FullName);
        var audits=db.database.GetCollection<BsonDocument>(AccountStateStore.AuditCollection);
        using var session=await db.mongoClient.StartSessionAsync();
        return await session.WithTransactionAsync(async (transaction,cancellation)=>
        {
            var account=await accounts.Find(transaction,a=>a.LoginName==request.LoginName).SingleOrDefaultAsync(cancellation);
            if(account==null)throw new AccountStateConflictException();
            var prior=await audits.Find(transaction,Builders<BsonDocument>.Filter.Eq("_id",request.OperationId)).FirstOrDefaultAsync(cancellation);
            if(prior!=null)
            {
                if(prior.GetValue("AccountId",0L).ToInt64()!=account.Id||prior.GetValue("Action","").AsString!="user-close-account"||
                    prior.GetValue("CredentialDigest","").AsString!=digest)throw new AccountStateConflictException();
                return false;
            }
            if(account.Status!=AccountStatus.Active||account.StateVersion!=expectedVersion||
                !PasswordHasher.Verify(request.Password,account.PasswordScheme,account.PasswordSalt,account.PasswordHash,account.PasswordIterations))
                throw new AccountStateConflictException();
            if(beforeUpdate!=null)await beforeUpdate();
            long closedAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var result=await accounts.UpdateOneAsync(transaction,
                a=>a.Id==account.Id&&a.Status==AccountStatus.Active&&a.StateVersion==expectedVersion,
                Builders<Account>.Update.Set(a=>a.Status,AccountStatus.Closed).Set(a=>a.ClosedAt,closedAt).Set(a=>a.StateVersion,expectedVersion+1),
                cancellationToken:cancellation);
            if(result.MatchedCount!=1)throw new AccountStateConflictException();
            beforeAudit?.Invoke();
            await audits.InsertOneAsync(transaction,new BsonDocument
            {
                {"_id",request.OperationId},{"AccountId",account.Id},{"Action","user-close-account"},
                {"ExpectedVersion",expectedVersion},{"Version",expectedVersion+1},{"Previous",(int)AccountStatus.Active},{"Target",(int)AccountStatus.Closed},
                {"OperatorId","account-self"},{"ReasonCode","user-request"},{"CredentialDigest",digest},{"CreatedAtUtc",DateTime.UtcNow}
            },cancellationToken:cancellation);
            return true;
        },new TransactionOptions(ReadConcern.Snapshot,ReadPreference.Primary,WriteConcern.WMajority));
    }
}

using System;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
internal static class UserPasswordStore
{
    // 在版本匹配时更新密码并写入审计。
    internal static async ETTask Change(DBComponent db,SelfPasswordChange request,long expectedVersion,string digest,Action beforeAudit=null,
        Func<System.Threading.Tasks.Task> beforeUpdate=null)
    {
        if(request==null||expectedVersion<0||expectedVersion==long.MaxValue||!Guid.TryParseExact(request.OperationId,"N",out _)||
            !AccountInputValidator.TryNormalizeLoginName(request.LoginName,out string normalized)||normalized!=request.LoginName||
            !AccountInputValidator.IsPasswordValid(request.OldPassword)||!AccountInputValidator.IsPasswordValid(request.NewPassword)||
            request.OldPassword==request.NewPassword||string.IsNullOrEmpty(digest)||digest.Length!=64)
            throw new AccountStateConflictException();
        var accounts=db.database.GetCollection<Account>(typeof(Account).FullName);
        var audits=db.database.GetCollection<BsonDocument>(AccountStateStore.AuditCollection);
        using var session=await db.mongoClient.StartSessionAsync();
        await session.WithTransactionAsync(async (transaction,cancellation)=>
        {
            var account=await accounts.Find(transaction,a=>a.LoginName==request.LoginName).SingleOrDefaultAsync(cancellation);
            if(account==null||account.Status!=AccountStatus.Active)throw new AccountStateConflictException();
            var prior=await audits.Find(transaction,Builders<BsonDocument>.Filter.Eq("_id",request.OperationId)).FirstOrDefaultAsync(cancellation);
            if(prior!=null)
            {
                if(prior["AccountId"].AsInt64!=account.Id||prior.GetValue("Action","").AsString!="user-change-password"||prior.GetValue("CredentialDigest","").AsString!=digest)
                    throw new AccountStateConflictException();
                return false;
            }
            if(account.StateVersion!=expectedVersion||expectedVersion==long.MaxValue||
                !PasswordHasher.Verify(request.OldPassword,account.PasswordScheme,account.PasswordSalt,account.PasswordHash,account.PasswordIterations))
                throw new AccountStateConflictException();
            if(beforeUpdate!=null)await beforeUpdate();
            var hash=PasswordHasher.Hash(request.NewPassword);
            var result=await accounts.UpdateOneAsync(transaction,a=>a.Id==account.Id,
                Builders<Account>.Update.Set(a=>a.PasswordHash,hash.Hash).Set(a=>a.PasswordSalt,hash.Salt)
                    .Set(a=>a.PasswordScheme,hash.Scheme).Set(a=>a.PasswordIterations,hash.Iterations).Set(a=>a.StateVersion,expectedVersion+1),cancellationToken:cancellation);
            if(result.MatchedCount!=1)throw new AccountStateConflictException();
            beforeAudit?.Invoke();
            await audits.InsertOneAsync(transaction,new BsonDocument
            {
                {"_id",request.OperationId},{"AccountId",account.Id},{"Action","user-change-password"},
                {"ExpectedVersion",expectedVersion},{"Version",expectedVersion+1},{"Previous",(int)account.Status},{"Target",(int)account.Status},
                {"OperatorId","account-self"},{"ReasonCode","user-request"},{"CredentialDigest",digest},{"CreatedAtUtc",DateTime.UtcNow}
            },cancellationToken:cancellation);
            return true;
        },new TransactionOptions(ReadConcern.Snapshot,ReadPreference.Primary,WriteConcern.WMajority));
    }
}

using System;
using System.IO;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
internal static class AccountDataExportStore
{
    internal const string AuditCollection="ET.Server.AccountDataRequestAudit";
    private const int MaximumRelatedRecords=10000;

    internal static async ETTask<bool> Export(DBComponent database,AccountAdminCommand command,string configuredRoot,bool production)
    {
        string root=ProductionSecretPolicy.ValidateDataExportRoot(configuredRoot,production);
        string output=Path.Combine(root,command.OperationId+".json");
        var audits=database.database.GetCollection<BsonDocument>(AuditCollection).WithWriteConcern(WriteConcern.WMajority);
        var prior=await audits.WithReadConcern(ReadConcern.Majority)
            .Find(Builders<BsonDocument>.Filter.Eq("_id",command.OperationId)).FirstOrDefaultAsync();
        if(prior!=null)
        {
            ValidatePrior(prior,command,output);
            return false;
        }

        var accounts=database.database.GetCollection<Account>(typeof(Account).FullName);
        Account account=await accounts.Find(a=>a.Id==command.AccountId).SingleOrDefaultAsync();
        if(account==null||account.StateVersion!=command.ExpectedVersion)throw new AccountStateConflictException();

        var roles=await ReadBounded(database,typeof(Role).FullName,Builders<BsonDocument>.Filter.Eq("AccountId",command.AccountId));
        var sessions=await ReadBounded(database,AccountSessionJournal.Collection,Builders<BsonDocument>.Filter.Eq("AccountId",command.AccountId),
            "Process","GateId","Zone","GateInstanceId");
        var stateAudits=await ReadBounded(database,AccountStateStore.AuditCollection,Builders<BsonDocument>.Filter.Eq("AccountId",command.AccountId),
            "CredentialDigest","OperatorId");
        var document=new BsonDocument
        {
            {"SchemaVersion",1},{"OperationId",command.OperationId},{"AccountId",command.AccountId},
            {"GeneratedAtUtc",DateTime.UtcNow},{"ReasonCode",command.ReasonCode},
            {"Account",new BsonDocument
                {
                    {"LoginName",account.LoginName??""},{"DisplayName",account.DisplayName??""},{"Status",(int)account.Status},
                    {"StateVersion",account.StateVersion},{"ClosedAt",account.ClosedAt},{"CreateTime",account.CreateTime},
                    {"LastLoginTime",account.LastLoginTime},{"DefaultRoleId",account.DefaultRoleId}
                }
            },
            {"Roles",roles},{"Sessions",sessions},{"StateAudits",stateAudits}
        };
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes(document.ToJson(new JsonWriterSettings {Indent=true,OutputMode=JsonOutputMode.RelaxedExtendedJson}));
        string digest=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string temporary=output+".tmp-"+Guid.NewGuid().ToString("N");
        bool outputCreated=false;
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(true);
            }
            File.Move(temporary,output,false);
            outputCreated=true;
            await audits.InsertOneAsync(new BsonDocument
            {
                {"_id",command.OperationId},{"Action","export"},{"AccountId",command.AccountId},
                {"ExpectedVersion",command.ExpectedVersion},{"OperatorId",command.OperatorId},{"ReasonCode",command.ReasonCode},
                {"OutputName",Path.GetFileName(output)},{"Sha256",digest},{"CreatedAtUtc",DateTime.UtcNow}
            });
            return true;
        }
        catch(Exception original)
        {
            DeleteExact(temporary);
            if(!outputCreated)throw;
            try
            {
                var confirmed=await audits.WithReadConcern(ReadConcern.Majority)
                    .Find(Builders<BsonDocument>.Filter.Eq("_id",command.OperationId)).FirstOrDefaultAsync();
                if(confirmed!=null)
                {
                    ValidatePrior(confirmed,command,output);
                    return true;
                }
                DeleteExact(output);
            }
            catch(Exception verification)
            {
                throw new InvalidOperationException("Account export completion is uncertain; preserve the output for audit",new AggregateException(original,verification));
            }
            throw;
        }
    }

    private static async ETTask<BsonArray> ReadBounded(DBComponent database,string collection,FilterDefinition<BsonDocument> filter,params string[] excluded)
    {
        var records=await database.database.GetCollection<BsonDocument>(collection).Find(filter).Limit(MaximumRelatedRecords+1).ToListAsync();
        if(records.Count>MaximumRelatedRecords)throw new InvalidOperationException("Account export record limit exceeded");
        var result=new BsonArray();
        foreach(var record in records)
        {
            record.Remove("PasswordHash");record.Remove("PasswordSalt");record.Remove("EncryptedPassword");
            foreach(string field in excluded)record.Remove(field);
            result.Add(record);
        }
        return result;
    }

    private static void ValidatePrior(BsonDocument prior,AccountAdminCommand command,string output)
    {
        if(prior.GetValue("Action","").AsString!="export"||prior.GetValue("AccountId",0L).ToInt64()!=command.AccountId||
            prior.GetValue("ExpectedVersion",-1L).ToInt64()!=command.ExpectedVersion||prior.GetValue("OperatorId","").AsString!=command.OperatorId||
            prior.GetValue("ReasonCode","").AsString!=command.ReasonCode||prior.GetValue("OutputName","").AsString!=Path.GetFileName(output)||!File.Exists(output))
            throw new InvalidOperationException("Operation id belongs to another or incomplete export");
        string digest=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant();
        if(!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(digest),System.Text.Encoding.ASCII.GetBytes(prior.GetValue("Sha256","").AsString)))
            throw new InvalidOperationException("Existing account export integrity check failed");
    }

    private static void DeleteExact(string path)
    {
        try { if(File.Exists(path))File.Delete(path); }
        catch(Exception) { Log.Warning("event=account_export_cleanup_failed"); }
    }
}

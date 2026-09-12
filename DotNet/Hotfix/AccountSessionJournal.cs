using System;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
[FriendOf(typeof(AccountSessionRecordComponent))]
public static class AccountSessionJournal
{
    public const string Collection="ET.Server.AccountSessionJournal";

    public static async ETTask Initialize(Scene root)
    {
        var db=root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
        await db.database.GetCollection<BsonDocument>(Collection).Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("AccountId").Descending("OpenedAtUtc"),
                new CreateIndexOptions {Name="ix_session_account_opened"}));
    }

    // Admission is not proof of a delivered login response or of current online status.
    public static async ETTask<bool> Begin(Session session,long accountId,long version)
    {
        if(session.IsDisposed||session.GetComponent<AccountSessionRecordComponent>()!=null)return false;
        long instance=session.InstanceId;
        var root=session.Root();
        var db=root.GetComponent<DBManagerComponent>().GetZoneDB(root.Zone());
        var records=db.database.GetCollection<BsonDocument>(Collection).WithWriteConcern(WriteConcern.WMajority);
        string recordId=Guid.NewGuid().ToString("N");
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await records.InsertOneAsync(new BsonDocument
            {
                {"_id",recordId},{"AccountId",accountId},{"AccountStateVersion",version},
                {"Process",Options.Instance.Process},{"GateId",root.Id},{"Zone",root.Zone()},
                {"OpenedAtUtc",DateTime.UtcNow},{"State","admitted"}
            },cancellationToken:timeout.Token);
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalAdmitted);
            if(session.IsDisposed||session.InstanceId!=instance)
            {
                await End(records,recordId,ErrorCore.ERR_InvalidAccountOrPassword);
                return false;
            }
            session.AddComponent<AccountSessionRecordComponent>().RecordId=recordId;
            return true;
        }
        catch(Exception)
        {
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalAdmissionFailures);
            Log.Warning("event=account_session_journal_failed stage=admission");
            return false;
        }
    }

    internal static async ETTask End(IMongoCollection<BsonDocument> records,string recordId,int error)
    {
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result=await records.WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id",recordId)&Builders<BsonDocument>.Filter.Eq("State","admitted"),
                Builders<BsonDocument>.Update.Set("State","ended").Set("EndedAtUtc",DateTime.UtcNow).Set("DisconnectError",error),
                cancellationToken:timeout.Token);
            if(result.ModifiedCount==1)OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEnded);
        }
        catch(Exception)
        {
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEndFailures);
            Log.Warning("event=account_session_journal_failed stage=disconnect");
        }
    }
}

[EntitySystemOf(typeof(AccountSessionRecordComponent))]
[FriendOf(typeof(AccountSessionRecordComponent))]
[FriendOf(typeof(DBComponent))]
public static partial class AccountSessionRecordComponentSystem
{
    [EntitySystem]
    private static void Awake(this AccountSessionRecordComponent self) { }

    [EntitySystem]
    private static void Destroy(this AccountSessionRecordComponent self)
    {
        if(string.IsNullOrEmpty(self.RecordId))return;
        var root=self.Root();
        if(root.IsDisposed){OperationalMetrics.Instance?.RecordSecurity(SecurityMetric.JournalEndSkipped);return;}
        var db=root.GetComponent<DBManagerComponent>()?.GetZoneDB(root.Zone());
        if(db==null||db.IsDisposed){OperationalMetrics.Instance?.RecordSecurity(SecurityMetric.JournalEndSkipped);return;}
        var records=db.database.GetCollection<BsonDocument>(AccountSessionJournal.Collection);
        AccountSessionJournal.End(records,self.RecordId,self.GetParent<Session>().Error).Coroutine();
    }
}

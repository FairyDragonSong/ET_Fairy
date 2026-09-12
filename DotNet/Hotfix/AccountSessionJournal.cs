using System;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ET.Server;

[FriendOf(typeof(DBComponent))]
[FriendOf(typeof(AccountSessionRecordComponent))]
[FriendOf(typeof(AccountSessionDrainComponent))]
public static class AccountSessionJournal
{
    public const string Collection="ET.Server.AccountSessionJournal";

    public static async ETTask Initialize(Scene root)
    {
        root.AddComponent<AccountSessionDrainComponent>();
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
                await EndTracked(root,records,recordId,ErrorCore.ERR_InvalidAccountOrPassword);
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
        await EndCore(records,recordId,error);
    }

    internal static async ETTask EndTracked(Scene root,IMongoCollection<BsonDocument> records,string recordId,int error)
    {
        var state=root.GetComponent<AccountSessionDrainComponent>().State;
        await EndQueued(state,records,recordId,error);
    }

    internal static async ETTask EndQueued(AccountSessionDrainState state,IMongoCollection<BsonDocument> records,string recordId,int error)
    {
        // Cap retained work as well as active writes. Overflow is observable and fails drain.
        if(Interlocked.Increment(ref state.Pending)>4096)
        {
            Interlocked.Decrement(ref state.Pending);
            Interlocked.Increment(ref state.Failures);
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEndFailures);
            Log.Warning("event=account_session_journal_failed stage=disconnect reason=queue_full");
            return;
        }
        bool acquired=false;
        try
        {
            await state.Slots.WaitAsync();
            acquired=true;
            if(!await EndCore(records,recordId,error))Interlocked.Increment(ref state.Failures);
        }
        finally
        {
            if(acquired)state.Slots.Release();
            Interlocked.Decrement(ref state.Pending);
        }
    }

    public static async ETTask<bool> Drain(Scene root)
    {
        var component=root.GetComponent<AccountSessionDrainComponent>();
        if(component==null)return true;
        var state=component.State;
        while(Volatile.Read(ref state.Pending)>0)
            await root.GetComponent<TimerComponent>().WaitAsync(20);
        bool success=Volatile.Read(ref state.Failures)==0;
        Log.Info($"event=account_session_journal_drained success={success} failures={state.Failures}");
        return success;
    }

    private static async ETTask<bool> EndCore(IMongoCollection<BsonDocument> records,string recordId,int error)
    {
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result=await records.WithWriteConcern(WriteConcern.WMajority).UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id",recordId)&Builders<BsonDocument>.Filter.Eq("State","admitted"),
                Builders<BsonDocument>.Update.Set("State","ended").Set("EndedAtUtc",DateTime.UtcNow).Set("DisconnectError",error),
                cancellationToken:timeout.Token);
            if(result.ModifiedCount==1)
            {
                OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEnded);
                return true;
            }
            // A successful command with no matching row is not proof of completion.
            // Verify an idempotent retry against majority-visible terminal evidence.
            var existing=await records.WithReadConcern(ReadConcern.Majority)
                .Find(Builders<BsonDocument>.Filter.Eq("_id",recordId))
                .Project(Builders<BsonDocument>.Projection.Include("State").Include("EndedAtUtc").Include("DisconnectError"))
                .FirstOrDefaultAsync(timeout.Token);
            if(existing!=null && existing.TryGetValue("State",out var status) && status.IsString && status.AsString=="ended" &&
                existing.TryGetValue("EndedAtUtc",out var ended) && ended.IsBsonDateTime &&
                existing.TryGetValue("DisconnectError",out var disconnect) && disconnect.IsInt32)
                return true;
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEndFailures);
            Log.Warning($"event=account_session_journal_failed stage=disconnect reason={(existing==null?"missing_record":"invalid_terminal")}");
            return false;
        }
        catch(Exception exception)
        {
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.JournalEndFailures);
            string reason=exception is OperationCanceledException?"timeout":exception is MongoCommandException?"command":exception is MongoWriteException?"write":"other";
            Log.Warning($"event=account_session_journal_failed stage=disconnect reason={reason}");
            return false;
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
        AccountSessionJournal.EndTracked(root,records,self.RecordId,self.GetParent<Session>().Error).Coroutine();
    }
}

[EntitySystemOf(typeof(AccountSessionDrainComponent))]
[FriendOf(typeof(AccountSessionDrainComponent))]
public static partial class AccountSessionDrainComponentSystem
{
    [EntitySystem]
    private static void Awake(this AccountSessionDrainComponent self)
    {
        self.State=new AccountSessionDrainState();
    }
}

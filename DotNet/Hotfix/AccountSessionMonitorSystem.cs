using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MongoDB.Driver;

namespace ET.Server;

[EntitySystemOf(typeof(AccountSessionMonitor))]
[FriendOf(typeof(AccountSessionMonitor))]
[FriendOf(typeof(PlayerComponent))]
[FriendOf(typeof(DBComponent))]
public static partial class AccountSessionMonitorSystem
{
    [EntitySystem]
    private static void Awake(this AccountSessionMonitor self)
    {
        self.Timer=self.Root().GetComponent<TimerComponent>().NewRepeatedTimer(5000,TimerInvokeType.AccountSessionCheck,self);
    }

    [EntitySystem]
    private static void Destroy(this AccountSessionMonitor self)
    {
        self.Root().GetComponent<TimerComponent>()?.Remove(ref self.Timer);
    }

    [Invoke(TimerInvokeType.AccountSessionCheck)]
    public sealed class CheckTimer : ATimer<AccountSessionMonitor>
    {
        protected override void Run(AccountSessionMonitor self) => self.Tick();
    }

    private static void Tick(this AccountSessionMonitor self)
    {
        long now=TimeInfo.Instance.ServerNow();
        List<(EntityRef<Player> Player,EntityRef<Session> Session,string Account)> captured=new();
        foreach(var reference in self.Root().GetComponent<PlayerComponent>().dictionary.Values.ToArray())
        {
            Player player=reference;
            Session session=player?.GetComponent<PlayerSessionComponent>()?.Session;
            if(session==null || session.IsDisposed)continue;
            if(now-player.AccountVerifiedAt>120000)
            {
                session.Dispose();
                Log.Warning("event=account_session_revoked reason=verification_expired");
                continue;
            }
            captured.Add((player,session,player.Account));
        }
        if(self.Checking || captured.Count==0)return;
        self.Checking=true;
        self.Check(captured).Coroutine();
    }

    private static async ETTask Check(this AccountSessionMonitor self,
        List<(EntityRef<Player> Player,EntityRef<Session> Session,string Account)> captured)
    {
        long instance=self.InstanceId;
        try
        {
            var db=self.Root().GetComponent<DBManagerComponent>().GetZoneDB(self.Zone());
            var collection=db.database.GetCollection<Account>(typeof(Account).FullName);
            for(int offset=0;offset<captured.Count;offset+=500)
            {
                var batch=captured.Skip(offset).Take(500).ToArray();
                var names=batch.Select(item=>item.Account).ToArray();
                using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var states=await collection.Find(a=>names.Contains(a.LoginName))
                    .Project(a=>new { a.LoginName,a.Status,a.StateVersion }).ToListAsync(cancellation.Token);
                if(self.IsDisposed || self.InstanceId!=instance)return;
                foreach(var item in batch)
                {
                    Player player=item.Player;
                    Session session=item.Session;
                    if(player==null || session==null || session.IsDisposed || player.GetComponent<PlayerSessionComponent>()?.Session!=session)continue;
                    var matches=states.Where(a=>a.LoginName==item.Account).ToArray();
                    if(matches.Length!=1 || matches[0].Status!=AccountStatus.Active || matches[0].StateVersion!=player.AccountStateVersion)
                    {
                        session.Dispose();
                        Log.Warning("event=account_session_revoked reason=state_changed");
                    }
                    else player.AccountVerifiedAt=TimeInfo.Instance.ServerNow();
                }
            }
        }
        catch(Exception)
        {
            // No credentials or account data in operational logs. The lease still expires.
            Log.Warning("event=account_session_check_failed");
        }
        finally
        {
            if(!self.IsDisposed && self.InstanceId==instance)self.Checking=false;
        }
    }
}

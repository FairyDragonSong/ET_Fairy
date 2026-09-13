using System;
using System.Security.Cryptography;
using System.Text;

namespace ET.Server;

[MessageSessionHandler(SceneType.Gate)]
public sealed class C2G_CloseAccountHandler:MessageSessionHandler<C2G_CloseAccount,G2C_CloseAccount>
{
    protected override async ETTask Run(Session session,C2G_CloseAccount request,G2C_CloseAccount response)
    {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Player player=session.GetComponent<SessionPlayerComponent>()?.Player;
            if(player==null||player.GetComponent<PlayerSessionComponent>()?.Session!=session)throw new AccountStateConflictException();
            if(!SelfPasswordChangeLimits.TryAcquire(player.Account,Environment.TickCount64))
            {
                OperationalMetrics.Instance.RecordSecurity(SecurityMetric.AccountCloseRateLimited);
                response.Error=ErrorCore.ERR_LoginRateLimited;response.Message="操作过于频繁";return;
            }
            string privateKey=ProductionSecretPolicy.ReadPasswordPrivateKey(SelfPasswordCipher.GetKeyId(request.Envelope),Options.Instance.Develop==0);
            SelfAccountClosure closure=SelfPasswordCipher.DecryptClosure(request.Envelope,privateKey);
            if(closure.LoginName!=player.Account)throw new AccountStateConflictException();
            long playerInstance=player.InstanceId,sessionInstance=session.InstanceId;
            using(await session.Root().GetComponent<CoroutineLockComponent>().Wait(CoroutineLockType.PlayerSession,player.Account.GetLongHashCode()))
            {
                if(session.IsDisposed||player.IsDisposed||player.InstanceId!=playerInstance||session.InstanceId!=sessionInstance||
                    player.GetComponent<PlayerSessionComponent>()?.Session!=session)throw new AccountStateConflictException();
                string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Envelope)));
                var db=session.Root().GetComponent<DBManagerComponent>().GetZoneDB(session.Zone());
                await UserAccountClosureStore.Close(db,closure,player.AccountStateVersion,digest);
                player.AccountStateVersion++;
            }
            response.Message="账号已注销";
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.AccountCloseAccepted);
            DisconnectAfterResponse(session,session.InstanceId).Coroutine();
        }
        catch(Exception)
        {
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.AccountCloseUnconfirmed);
            response.Error=ErrorCore.ERR_InvalidAccountOrPassword;
            response.Message="账号注销未确认成功，请核对账号状态后重试";
            Log.Warning("event=user_account_close_rejected");
        }
        finally
        {
            OperationalMetrics.Instance?.RecordHandlerDuration(HandlerLatency.AccountClose,
                (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private static async ETTask DisconnectAfterResponse(Session session,long instanceId)
    {
        await session.Root().GetComponent<TimerComponent>().WaitAsync(100);
        if(!session.IsDisposed&&session.InstanceId==instanceId)session.Dispose();
    }
}

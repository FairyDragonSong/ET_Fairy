using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ET.Server;

[MessageSessionHandler(SceneType.Gate)]
public sealed class C2G_ChangePasswordHandler : MessageSessionHandler<C2G_ChangePassword,G2C_ChangePassword>
{
    protected override async ETTask Run(Session session,C2G_ChangePassword request,G2C_ChangePassword response)
    {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            Player player=session.GetComponent<SessionPlayerComponent>()?.Player;
            if(player==null||player.GetComponent<PlayerSessionComponent>()?.Session!=session)throw new AccountStateConflictException();
            if(!SelfPasswordChangeLimits.TryAcquire(player.Account,Environment.TickCount64))
            {
                OperationalMetrics.Instance.RecordSecurity(SecurityMetric.PasswordChangeRateLimited);
                response.Error=ErrorCore.ERR_LoginRateLimited;response.Message="操作过于频繁";return;
            }
            string keyPath=Environment.GetEnvironmentVariable("ET_ACCOUNT_PASSWORD_PRIVATE_KEY_FILE");
            if(string.IsNullOrEmpty(keyPath)||new FileInfo(keyPath).Length>16384)throw new AccountStateConflictException();
            var change=SelfPasswordCipher.Decrypt(request.Envelope,File.ReadAllText(keyPath));
            if(change.LoginName!=player.Account)throw new AccountStateConflictException();
            long playerInstance=player.InstanceId,sessionInstance=session.InstanceId;
            using(await session.Root().GetComponent<CoroutineLockComponent>().Wait(CoroutineLockType.PlayerSession,player.Account.GetLongHashCode()))
            {
                if(session.IsDisposed||player.IsDisposed||player.InstanceId!=playerInstance||session.InstanceId!=sessionInstance||player.GetComponent<PlayerSessionComponent>()?.Session!=session)throw new AccountStateConflictException();
                string digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Envelope)));
                var db=session.Root().GetComponent<DBManagerComponent>().GetZoneDB(session.Zone());
                await UserPasswordStore.Change(db,change,player.AccountStateVersion,digest);
            }
            response.Message="密码已更新，请重新登录";
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.PasswordChangeAccepted);
        }
        catch(Exception)
        {
            OperationalMetrics.Instance.RecordSecurity(SecurityMetric.PasswordChangeUnconfirmed);
            response.Error=ErrorCore.ERR_InvalidAccountOrPassword;
            response.Message="密码修改未确认成功，请核对凭据后重试";
            Log.Warning("event=user_password_change_rejected");
        }
        finally
        {
            OperationalMetrics.Instance?.RecordHandlerDuration(HandlerLatency.PasswordChange,(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency);
        }
    }
}

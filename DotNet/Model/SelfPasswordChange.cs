using System;
using System.Collections.Generic;
using System.Linq;

namespace ET.Server;

[EnableClass]
public sealed class SelfPasswordChange
{
    public string OperationId { get; set; }
    public string LoginName { get; set; }
    public string OldPassword { get; set; }
    public string NewPassword { get; set; }
    public long ExpiresAt { get; set; }
}

[EnableClass]
public sealed class PasswordChangeCipher
{
    public string KeyId { get; set; }
    public string Key { get; set; }
    public string Nonce { get; set; }
    public string Ciphertext { get; set; }
    public string Tag { get; set; }
}

public static class SelfPasswordChangeLimits
{
    [StaticField] private static readonly AccountAdminRateLimiter global=new(30,60000);
    [StaticField] private static readonly Dictionary<string,(AccountAdminRateLimiter Limiter,long Seen)> accounts=new();
    public static bool TryAcquire(string account,long now)
    {
        if(!global.TryAcquire(now))return false;
        lock(accounts)
        {
            if(!accounts.TryGetValue(account,out var entry))
            {
                if(accounts.Count>=10000)
                    foreach(var key in accounts.Where(p=>now-p.Value.Seen>=60000).Select(p=>p.Key).ToArray())accounts.Remove(key);
                if(accounts.Count>=10000)return false;
                entry=(new AccountAdminRateLimiter(5,60000),now);
            }
            accounts[account]=(entry.Limiter,now);
            return entry.Limiter.TryAcquire(now);
        }
    }
}

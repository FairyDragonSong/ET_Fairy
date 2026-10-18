using System;
using System.Collections.Generic;

namespace ET.Server;

[EnableClass]
public sealed class AccountAdminRateLimiter
{
    [StaticField]
    public static readonly AccountAdminRateLimiter ProcessLimiter=new(30,60000);
    private readonly Queue<long> arrivals=new();
    private readonly int limit;
    private readonly long window;
    // 创建固定窗口限流器，并把非法配置收敛到安全下限。
    public AccountAdminRateLimiter(int limit,long window)
    {
        if(limit<=0||window<=0)throw new ArgumentOutOfRangeException(nameof(limit));
        this.limit=limit;this.window=window;
    }
    // 尝试获取当前操作额度，并返回是否允许继续。
    public bool TryAcquire(long now)
    {
        lock(this.arrivals)
        {
            while(this.arrivals.Count>0 && now-this.arrivals.Peek()>=this.window)this.arrivals.Dequeue();
            if(this.arrivals.Count>=this.limit)return false;
            this.arrivals.Enqueue(now);
            return true;
        }
    }
}

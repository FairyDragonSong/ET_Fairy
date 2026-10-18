using System;

namespace ET.Server;

[EnableClass]
public sealed class AccountStateConflictException : InvalidOperationException
{
    // 统一表示账号版本、状态或并发前置条件冲突。
    public AccountStateConflictException() : base("Account state conflict") { }
}

public static class AccountStatePolicy
{
    // 根据当前状态和管理动作计算目标账号状态。
    public static AccountStatus Target(AccountStatus current,string action)
    {
        if(current!=AccountStatus.Active && current!=AccountStatus.Disabled)throw new AccountStateConflictException();
        return action switch
        {
            "enable" when current==AccountStatus.Disabled => AccountStatus.Active,
            "disable" when current==AccountStatus.Active => AccountStatus.Disabled,
            "revoke" => current,
            "reset-password" => current,
            "close" => AccountStatus.Closed,
            _ => throw new AccountStateConflictException()
        };
    }
}

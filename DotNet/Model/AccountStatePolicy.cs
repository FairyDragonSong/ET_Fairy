using System;

namespace ET.Server;

[EnableClass]
public sealed class AccountStateConflictException : InvalidOperationException
{
    public AccountStateConflictException() : base("Account state conflict") { }
}

public static class AccountStatePolicy
{
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

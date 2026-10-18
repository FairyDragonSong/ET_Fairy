namespace ET
{
    public interface ISensitiveMessage
    {
    }

    public partial class C2G_ChangePassword: ISensitiveMessage { }
    public partial class G2C_ChangePassword: ISensitiveMessage { }
    public partial class C2G_CloseAccount: ISensitiveMessage { }
    public partial class G2C_CloseAccount: ISensitiveMessage { }

    public partial class Main2NetClient_Login: ISensitiveMessage
    {
    }

    public partial class C2M_Reload: ISensitiveMessage
    {
    }

    public partial class C2R_Login: ISensitiveMessage
    {
    }

    public partial class R2C_Login: ISensitiveMessage
    {
    }

    public partial class C2G_LoginGate: ISensitiveMessage
    {
    }

    // 客户端不生成该内部消息，允许安全标记的 partial 独立存在。
    [EnableClass]
    public partial class R2G_GetLoginKey: ISensitiveMessage
    {
    }

    [EnableClass]
    public partial class G2R_GetLoginKey: ISensitiveMessage
    {
    }
}

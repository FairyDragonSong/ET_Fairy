namespace ET
{
    public interface ISensitiveMessage
    {
    }

    public partial class C2G_ChangePassword: ISensitiveMessage { }
    public partial class G2C_ChangePassword: ISensitiveMessage { }

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

    public partial class R2G_GetLoginKey: ISensitiveMessage
    {
    }

    public partial class G2R_GetLoginKey: ISensitiveMessage
    {
    }
}

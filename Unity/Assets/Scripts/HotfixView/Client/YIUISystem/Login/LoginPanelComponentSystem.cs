using System;
using YIUIFramework;
using System.Collections.Generic;

namespace ET.Client
{
    [FriendOf(typeof(LoginPanelComponent))]
    public static partial class LoginPanelComponentSystem
    {
        [EntitySystem]
        private static void YIUIInitialize(this LoginPanelComponent self)
        {
        }

        [EntitySystem]
        private static void Destroy(this LoginPanelComponent self)
        {
        }

        [EntitySystem]
        private static async ETTask<bool> YIUIOpen(this LoginPanelComponent self)
        {
            await ETTask.CompletedTask;
            return true;
        }

        #region YIUIEvent开始
        
        private static void OnEventPasswordAction(this LoginPanelComponent self, string p1)
        {
            self.Password = p1;
        }
        
        private static void OnEventAccountAction(this LoginPanelComponent self, string p1)
        {
            self.Account = p1;
        }
        
        private static async ETTask OnEventLoginAction(this LoginPanelComponent self)
        {
            var banId = YIUIMgrComponent.Inst.BanLayerOptionForever();
            try
            {
                await LoginHelper.Login(self.Root(), self.Account, self.Password);
            }
            catch (RpcException e)
            {
                // 只展示本地映射的安全文案，不直接透传服务端内部错误。
                Log.Warning($"login failed, error: {e.Error}");
                TipsHelper.OpenSync<TextTipsViewComponent>(GetLoginErrorMessage(e.Error));
            }
            catch (Exception e)
            {
                Log.Error(e);
                TipsHelper.OpenSync<TextTipsViewComponent>("网络连接失败，请稍后重试");
            }
            finally
            {
                // 登录成功、失败或异常都必须恢复界面操作。
                YIUIMgrComponent.Inst.RecoverLayerOptionForever(banId);
            }
        }

        // 将登录错误收敛为用户可理解的提示。
        private static string GetLoginErrorMessage(int error)
        {
            return error switch
            {
                ErrorCore.ERR_AccountInputInvalid => "账号或密码格式错误",
                ErrorCore.ERR_InvalidAccountOrPassword => "账号或密码错误",
                ErrorCore.ERR_AccountDisabled => "账号或密码错误",
                ErrorCore.ERR_LoginRateLimited => "操作过于频繁，请稍后重试",
                ErrorCore.ERR_OuterSessionRateLimited => "操作过于频繁，请稍后重试",
                ErrorCore.ERR_AccountStorageUnavailable => "登录服务暂时不可用，请稍后重试",
                ErrorCore.ERR_RoleStorageUnavailable => "登录服务暂时不可用，请稍后重试",
                ErrorCore.ERR_PlayerStateConflict => "账号状态正在更新，请稍后重试",
                ErrorCore.ERR_AuthenticationProviderDisabled => "当前登录方式暂不可用",
                _ => "网络连接失败，请稍后重试",
            };
        }
        #endregion YIUIEvent结束
    }
}

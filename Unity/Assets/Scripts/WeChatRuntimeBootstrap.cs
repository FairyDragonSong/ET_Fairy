using UnityEngine;

public static class WeChatRuntimeBootstrap
{
#if (UNITY_WEBGL || UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME) && !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        var info = WeChatWASM.WX.GetSystemInfoSync();
        Debug.Log($"WeChat Runtime initialized: {info.platform}");

        ET.GlobalConfig globalConfig = Resources.Load<ET.GlobalConfig>("GlobalConfig");
        if (globalConfig != null)
        {
            ET.MiniGameNetworkSettings.Configure(
                globalConfig.MiniGameRouterHttpUrl,
                globalConfig.MiniGameRouterWebSocketUrl,
                globalConfig.MiniGameRouterWebSocketPortOffset);
        }
    }
#endif
}

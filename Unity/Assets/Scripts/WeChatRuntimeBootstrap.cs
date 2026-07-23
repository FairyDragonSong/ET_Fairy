using UnityEngine;

public static class WeChatRuntimeBootstrap
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Initialize()
    {
        var info = WeChatWASM.WX.GetSystemInfoSync();
        Debug.Log($"WeChat Runtime initialized: {info.platform}");
    }
#endif
}
using UnityEngine;
using YooAsset;

namespace ET
{
    public enum CodeMode
    {
        Client = 1,
        Server = 2,
        ClientServer = 3,
    }
    
    public enum BuildType
    {
        Debug,
        Release,
    }
    
    [CreateAssetMenu(menuName = "ET/CreateGlobalConfig", fileName = "GlobalConfig", order = 0)]
    public class GlobalConfig: ScriptableObject
    {
        public CodeMode CodeMode;

        public bool EnableDll;

        public BuildType BuildType;

        public AppType AppType;

        [Tooltip("启动本地红蓝群体对抗 Demo（无需登录服务器）。")]
        public bool EnableRedBlueDemo;

        public EPlayMode EPlayMode;

        [Tooltip("YooAsset CDN 根地址。运行时会在其后追加 CDN/{Platform}/{ResourceAppVersion}。")]
        public string ResourceHostServer = "http://127.0.0.1";

        [Tooltip("YooAsset 备用 CDN 根地址。留空时使用 ResourceHostServer。")]
        public string ResourceFallbackHostServer;

        [Tooltip("YooAsset 资源版本目录。")]
        public string ResourceAppVersion = "v1.0";

        [Tooltip("微信小游戏 RouterManager HTTPS 地址，例如 https://game.example.com/get_router。")]
        public string MiniGameRouterHttpUrl = "http://127.0.0.1:30300/get_router";

        [Tooltip("微信小游戏 Router WSS 地址模板：{0}=Router IP，{1}=Router 原端口，{2}=WebSocket 监听端口。")]
        public string MiniGameRouterWebSocketUrl = "ws://{0}:{2}/et/";

        [Tooltip("Router WebSocket 监听端口相对原 Router 端口的偏移。")]
        public int MiniGameRouterWebSocketPortOffset = 1000;
    }
}

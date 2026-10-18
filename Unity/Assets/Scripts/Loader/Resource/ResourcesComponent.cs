using System;
using System.Collections.Generic;
using UnityEngine;
using YooAsset;

namespace ET
{
    /// <summary>
    /// 远端资源地址查询服务类
    /// </summary>
    public class RemoteServices : IRemoteServices
    {
        private readonly string _defaultHostServer;
        private readonly string _fallbackHostServer;

        public RemoteServices(string defaultHostServer, string fallbackHostServer)
        {
            _defaultHostServer = defaultHostServer;
            _fallbackHostServer = fallbackHostServer;
        }

        string IRemoteServices.GetRemoteMainURL(string fileName)
        {
            return BuildRemoteURL(_defaultHostServer, fileName);
        }

        string IRemoteServices.GetRemoteFallbackURL(string fileName)
        {
            return BuildRemoteURL(_fallbackHostServer, fileName);
        }

        private static string BuildRemoteURL(string hostServer, string fileName)
        {
            string url = $"{hostServer}/{fileName}";
            // 版本入口禁止使用旧缓存，具体资源仍按文件哈希正常缓存。
            return fileName.EndsWith(".version", StringComparison.OrdinalIgnoreCase)
                    ? $"{url}?t={DateTime.UtcNow.Ticks}"
                    : url;
        }
    }

    public class ResourcesComponent : Singleton<ResourcesComponent>, ISingletonAwake
    {
        public void Awake()
        {
            YooAssets.Initialize();
        }

        protected override void Destroy()
        {
#if UNITY_WEBGL
            // YooAsset 在 WebGL 平台不支持强制卸载全部资源。
            return;
#else
            YooAssets.Destroy();
#endif
        }

        public async ETTask CreatePackageAsync(string packageName, bool isDefault = false)
        {
            ResourcePackage package = YooAssets.CreatePackage(packageName);
            if (isDefault)
            {
                YooAssets.SetDefaultPackage(package);
            }

            GlobalConfig globalConfig = Resources.Load<GlobalConfig>("GlobalConfig");
            EPlayMode ePlayMode = globalConfig.EPlayMode;
            InitializationOperation initializationOperation;

            // 编辑器下的模拟模式
            switch (ePlayMode)
            {
                case EPlayMode.EditorSimulateMode:
                {
                    EditorSimulateModeParameters createParameters = new();
                    createParameters.SimulateManifestFilePath = EditorSimulateModeHelper.SimulateBuild("ScriptableBuildPipeline", packageName);
                    initializationOperation = package.InitializeAsync(createParameters);
                    break;
                }
                case EPlayMode.OfflinePlayMode:
                {
                    OfflinePlayModeParameters createParameters = new();
                    initializationOperation = package.InitializeAsync(createParameters);
                    break;
                }
                case EPlayMode.HostPlayMode:
                {
                    string defaultHostServer = GetHostServerURL(globalConfig.ResourceHostServer, globalConfig.ResourceAppVersion);
                    string fallbackHostServer = GetHostServerURL(GetFallbackHostServer(globalConfig), globalConfig.ResourceAppVersion);
                    HostPlayModeParameters createParameters = new();
                    createParameters.BuildinQueryServices = new GameQueryServices();
                    createParameters.RemoteServices = new RemoteServices(defaultHostServer, fallbackHostServer);
                    initializationOperation = package.InitializeAsync(createParameters);
                    break;
                }
                case EPlayMode.WebPlayMode:
                {
                    string defaultHostServer = GetHostServerURL(globalConfig.ResourceHostServer, globalConfig.ResourceAppVersion);
                    string fallbackHostServer = GetHostServerURL(GetFallbackHostServer(globalConfig), globalConfig.ResourceAppVersion);
                    WebPlayModeParameters createParameters = new();
                    createParameters.BuildinQueryServices = new GameQueryServices();
                    createParameters.RemoteServices = new RemoteServices(defaultHostServer, fallbackHostServer);
                    initializationOperation = package.InitializeAsync(createParameters);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException();
            }

            await initializationOperation.Task;
            if (initializationOperation.Status != EOperationStatus.Succeed)
            {
                throw new Exception($"YooAsset package initialize failed: package={packageName}, playMode={ePlayMode}, error={initializationOperation.Error}");
            }

            Log.Info($"YooAsset package initialized: package={packageName}, playMode={ePlayMode}, version={initializationOperation.PackageVersion}");

            if (ePlayMode == EPlayMode.HostPlayMode || ePlayMode == EPlayMode.WebPlayMode)
            {
                // 初始化只加载内置清单；热更代码加载前需切换到远端最新清单。
                UpdatePackageVersionOperation versionOperation = package.UpdatePackageVersionAsync();
                await versionOperation.Task;
                if (versionOperation.Status != EOperationStatus.Succeed)
                {
                    Log.Warning($"YooAsset version update failed: package={packageName}, error={versionOperation.Error}");
                    return;
                }

                UpdatePackageManifestOperation manifestOperation = package.UpdatePackageManifestAsync(versionOperation.PackageVersion);
                await manifestOperation.Task;
                if (manifestOperation.Status != EOperationStatus.Succeed)
                {
                    Log.Warning($"YooAsset manifest update failed: package={packageName}, version={versionOperation.PackageVersion}, error={manifestOperation.Error}");
                    return;
                }

                Log.Info($"YooAsset package updated: package={packageName}, version={versionOperation.PackageVersion}");
            }
        }

        private static string GetFallbackHostServer(GlobalConfig globalConfig)
        {
            return string.IsNullOrWhiteSpace(globalConfig.ResourceFallbackHostServer)
                    ? globalConfig.ResourceHostServer
                    : globalConfig.ResourceFallbackHostServer;
        }

        private static string GetHostServerURL(string hostServer, string appVersion)
        {
            if (string.IsNullOrWhiteSpace(hostServer))
            {
                throw new InvalidOperationException("GlobalConfig.ResourceHostServer is empty.");
            }

            string platform;
#if UNITY_EDITOR
            if (UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.Android)
            {
                platform = "Android";
            }
            else if (UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.iOS)
            {
                platform = "IPhone";
            }
            else if (UnityEditor.EditorUserBuildSettings.activeBuildTarget == UnityEditor.BuildTarget.WebGL)
            {
                platform = "WebGL";
            }
            else if (UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString() == "MiniGame")
            {
                platform = "MiniGame";
            }
            else
            {
                platform = "PC";
            }
#else
            string runtimePlatform = Application.platform.ToString();
            // 团结引擎小游戏使用独立 RuntimePlatform，不能按 PC 资源目录处理。
            if (runtimePlatform == "WeixinMiniGamePlayer" || runtimePlatform == "MiniGamePlayer")
            {
                platform = "MiniGame";
            }
            else if (Application.platform == RuntimePlatform.Android)
            {
                platform = "Android";
            }
            else if (Application.platform == RuntimePlatform.IPhonePlayer)
            {
                platform = "IPhone";
            }
            else if (Application.platform == RuntimePlatform.WebGLPlayer)
            {
                // 兼容 Unity 转换方案与团结 MiniGame 平台提供的编译宏。
                #if UNITY_WEIXINMINIGAME || TUANJIE_WEIXINMINIGAME || WEIXINMINIGAME || PLATFORM_WEIXINMINIGAME
                platform = "MiniGame";
                #else
                platform = "WebGL";
                #endif
            }
            else
            {
                platform = "PC";
            }
#endif
            return $"{hostServer.TrimEnd('/')}/CDN/{platform}/{appVersion.Trim('/')}";
        }

        public void DestroyPackage(string packageName)
        {
            ResourcePackage package = YooAssets.GetPackage(packageName);
            package.UnloadUnusedAssets();
        }

        /// <summary>
        /// 主要用来加载dll config aotdll，因为这时候纤程还没创建，无法使用ResourcesLoaderComponent。
        /// 游戏中的资源应该使用ResourcesLoaderComponent来加载
        /// </summary>
        public async ETTask<T> LoadAssetAsync<T>(string location) where T : UnityEngine.Object
        {
            AssetHandle handle = YooAssets.LoadAssetAsync<T>(location);
            await handle.Task;
            T t = (T)handle.AssetObject;
            handle.Release();
            return t;
        }

        /// <summary>
        /// 主要用来加载dll config aotdll，因为这时候纤程还没创建，无法使用ResourcesLoaderComponent。
        /// 游戏中的资源应该使用ResourcesLoaderComponent来加载
        /// </summary>
        public async ETTask<Dictionary<string, T>> LoadAllAssetsAsync<T>(string location) where T : UnityEngine.Object
        {
            AllAssetsHandle allAssetsOperationHandle = YooAssets.LoadAllAssetsAsync<T>(location);
            await allAssetsOperationHandle.Task;
            Dictionary<string, T> dictionary = new Dictionary<string, T>();
            foreach (UnityEngine.Object assetObj in allAssetsOperationHandle.AllAssetObjects)
            {
                T t = assetObj as T;
                dictionary.Add(t.name, t);
            }

            allAssetsOperationHandle.Release();
            return dictionary;
        }
    }
}

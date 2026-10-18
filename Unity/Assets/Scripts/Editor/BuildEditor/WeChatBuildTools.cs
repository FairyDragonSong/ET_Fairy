using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace ET
{
    public static class WeChatBuildTools
    {
        // Keep all platform preflight checks here so menu builds and player builds share the same gate.
        private const string PackageName = "DefaultPackage";
        private const string GlobalConfigPath = "Assets/Resources/GlobalConfig.asset";
        private const string InitScenePath = "Assets/Scenes/Init.unity";
        private const string MiniGameConfigPath = "Assets/WX-WASM-SDK-V2/Editor/MiniGameConfig.asset";

        [MenuItem("ET/WeChat/Validate Build", false, ETMenuItemPriority.BuildTool + 20)]
        public static void ValidateBuildMenu()
        {
            ValidateBuild(true);
            EditorUtility.DisplayDialog("WeChat Build", "微信小游戏构建预检通过。", "OK");
        }

        [MenuItem("ET/WeChat/Build YooAsset Package", false, ETMenuItemPriority.BuildTool + 21)]
        public static void BuildYooAssetPackage()
        {
            EnsureMiniGameTarget();

            string packageVersion = DateTime.Now.ToString("yyyy-MM-dd-HHmm");
            BuiltinBuildParameters buildParameters = new()
            {
                BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot(),
                BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot(),
                BuildPipeline = nameof(BuiltinBuildPipeline),
                BuildTarget = EditorUserBuildSettings.activeBuildTarget,
                BuildMode = EBuildMode.ForceRebuild,
                PackageName = PackageName,
                PackageVersion = packageVersion,
                EnableSharePackRule = true,
                VerifyBuildingResult = true,
                FileNameStyle = EFileNameStyle.HashName,
                BuildinFileCopyOption = EBuildinFileCopyOption.ClearAndCopyAll,
                BuildinFileCopyParams = string.Empty,
                EncryptionServices = null,
                CompressOption = ECompressOption.LZ4,
                DisableWriteTypeTree = false,
                IgnoreTypeTreeChanges = true,
            };

            BuiltinBuildPipeline pipeline = new();
            YooAsset.Editor.BuildResult result = pipeline.Run(buildParameters, true);
            if (!result.Success)
            {
                throw new BuildFailedException(
                    $"YooAsset build failed: task={result.FailedTask}, error={result.ErrorInfo}");
            }

            int buildinFileCount = BuildinFileManifestBuilder.Build();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(
                $"微信 YooAsset 构建完成: target={buildParameters.BuildTarget}, version={packageVersion}, " +
                $"buildinFiles={buildinFileCount}, output={result.OutputPackageDirectory}");
        }

        [MenuItem("ET/WeChat/Publish YooAsset to Local CDN", false, ETMenuItemPriority.BuildTool + 22)]
        public static void PublishYooAssetToLocalCDN()
        {
            EnsureMiniGameTarget();
            string sourceDir = ValidateMiniGameResourceOutput();
            string versionFileName = $"PackageManifest_{PackageName}.version";
            string packageVersion = File.ReadAllText(Path.Combine(sourceDir, versionFileName)).Trim();

            GlobalConfig globalConfig = AssetDatabase.LoadAssetAtPath<GlobalConfig>(GlobalConfigPath);
            if (globalConfig == null)
            {
                throw new BuildFailedException($"GlobalConfig not found: {GlobalConfigPath}");
            }

            string appVersion = globalConfig.ResourceAppVersion;
            ValidateDirectoryName(appVersion, "GlobalConfig.ResourceAppVersion");

            UnityEngine.Object config = AssetDatabase.LoadMainAssetAtPath(MiniGameConfigPath);
            SerializedProperty projectConf = config == null ? null : new SerializedObject(config).FindProperty("ProjectConf");
            string relativeOutput = projectConf?.FindPropertyRelative("relativeDST")?.stringValue;
            if (string.IsNullOrWhiteSpace(relativeOutput) || Path.IsPathRooted(relativeOutput))
            {
                throw new BuildFailedException("微信转换配置的 relativeDST 必须是项目内的相对目录。");
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string exportRoot = Path.GetFullPath(Path.Combine(projectRoot, relativeOutput));
            if (!exportRoot.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException($"微信转换输出目录不在 Unity 工程内：{exportRoot}");
            }

            string targetDir = Path.Combine(exportRoot, "webgl", "CDN", "MiniGame", appVersion);
            string[] sourceFiles = Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly);
            if (!File.Exists(Path.Combine(sourceDir, $"PackageManifest_{PackageName}_{packageVersion}.bytes")) ||
                !sourceFiles.Any(file => file.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BuildFailedException($"YooAsset 构建产物不完整：{sourceDir}");
            }

            Directory.CreateDirectory(targetDir);
            int copied = 0;
            // 先复制资源和清单，最后切换版本入口。
            foreach (string sourceFile in sourceFiles)
            {
                if (Path.GetFileName(sourceFile) == versionFileName)
                {
                    continue;
                }

                File.Copy(sourceFile, Path.Combine(targetDir, Path.GetFileName(sourceFile)), true);
                ++copied;
            }

            File.Copy(Path.Combine(sourceDir, versionFileName), Path.Combine(targetDir, versionFileName), true);
            Debug.Log($"微信 YooAsset 已发布到本地 CDN: packageVersion={packageVersion}, appVersion={appVersion}, files={copied + 1}, target={targetDir}");
        }

        internal static void ValidateBuild(bool validateRemoteSettings)
        {
            EnsureMiniGameTarget();

            GlobalConfig globalConfig = AssetDatabase.LoadAssetAtPath<GlobalConfig>(GlobalConfigPath);
            if (globalConfig == null)
            {
                throw new BuildFailedException($"GlobalConfig not found: {GlobalConfigPath}");
            }

            if (globalConfig.CodeMode != CodeMode.Client)
            {
                throw new BuildFailedException("微信小游戏要求 GlobalConfig.CodeMode = Client。");
            }

            if (globalConfig.EPlayMode != EPlayMode.WebPlayMode)
            {
                throw new BuildFailedException("微信小游戏要求 GlobalConfig.EPlayMode = WebPlayMode。");
            }

            EditorBuildSettingsScene firstEnabledScene =
                    EditorBuildSettings.scenes.FirstOrDefault(scene => scene.enabled);
            if (firstEnabledScene == null || firstEnabledScene.path != InitScenePath)
            {
                throw new BuildFailedException($"微信小游戏的第一个启用场景必须是 {InitScenePath}。");
            }

            ValidateMiniGameResourceOutput();
            ValidateHybridCLROutput();

            if (validateRemoteSettings)
            {
                ValidateRemoteURL("GlobalConfig.ResourceHostServer", globalConfig.ResourceHostServer, globalConfig.BuildType);
                ValidateRemoteURL("GlobalConfig.MiniGameRouterHttpUrl", globalConfig.MiniGameRouterHttpUrl, globalConfig.BuildType);
                ValidateWebSocketURL(globalConfig.MiniGameRouterWebSocketUrl, globalConfig.BuildType);
                ValidateMiniGameSDKCDN(globalConfig.BuildType);
            }
        }

        private static void EnsureMiniGameTarget()
        {
            if (EditorUserBuildSettings.activeBuildTarget.ToString() != "MiniGame")
            {
                throw new BuildFailedException(
                    $"当前构建目标是 {EditorUserBuildSettings.activeBuildTarget}，请先切换到 WeixinMiniGame/MiniGame。");
            }
        }

        private static string ValidateMiniGameResourceOutput()
        {
            string outputRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", "Bundles", "MiniGame", PackageName));
            if (!Directory.Exists(outputRoot))
            {
                throw new BuildFailedException(
                    $"缺少微信目标 YooAsset 产物：{outputRoot}。请执行 ET/WeChat/Build YooAsset Package。");
            }

            string streamingVersionPath = Path.Combine(
                Application.streamingAssetsPath,
                StreamingAssetsDefine.RootFolderName,
                PackageName,
                $"PackageManifest_{PackageName}.version");
            if (!File.Exists(streamingVersionPath))
            {
                throw new BuildFailedException($"StreamingAssets 缺少 YooAsset 版本文件：{streamingVersionPath}");
            }

            string builtVersion = File.ReadAllText(streamingVersionPath).Trim();
            ValidateDirectoryName(builtVersion, "YooAsset package version");
            string expectedManifestPath = Path.Combine(
                outputRoot,
                builtVersion,
                $"PackageManifest_{PackageName}.version");
            if (!File.Exists(expectedManifestPath))
            {
                throw new BuildFailedException(
                    $"StreamingAssets 版本 {builtVersion} 不是当前 MiniGame 构建产物，请重新构建 YooAsset。");
            }

            return Path.GetDirectoryName(expectedManifestPath);
        }

        private static void ValidateDirectoryName(string value, string settingName)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
                value != Path.GetFileName(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new BuildFailedException($"{settingName} 必须是单个有效目录名：{value}");
            }
        }

        private static void ValidateHybridCLROutput()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string strippedAOTDir = Path.Combine(
                projectRoot, "HybridCLRData", "AssembliesPostIl2CppStrip", "MiniGame");

            if (!Directory.Exists(strippedAOTDir))
            {
                throw new BuildFailedException($"缺少 MiniGame AOT 裁剪程序集：{strippedAOTDir}");
            }

            ValidateHotUpdateAssemblies(projectRoot);
            ValidateAOTAssemblies(strippedAOTDir);
        }

        private static void ValidateHotUpdateAssemblies(string projectRoot)
        {
            string sourceDir = Path.Combine(projectRoot, Define.BuildOutputDir);
            string assetDir = Path.Combine(projectRoot, Define.CodeDir);
            foreach (string assemblyName in AssemblyTool.DllNames)
            {
                string sourcePath = Path.Combine(sourceDir, $"{assemblyName}.dll");
                string assetPath = Path.Combine(assetDir, $"{assemblyName}.dll.bytes");
                ValidateCopiedAssembly(
                    sourcePath,
                    assetPath,
                    $"MiniGame 热更新程序集 {assemblyName} 缺失或已过期，请先执行 ET/Compile (F6)");
            }
        }

        private static void ValidateAOTAssemblies(string strippedAOTDir)
        {
            string assetDir = Path.Combine(Application.dataPath, "Bundles", "AotDlls");
            foreach (string assemblyName in HybridCLRSettings.Instance.patchAOTAssemblies)
            {
                string sourcePath = Path.Combine(strippedAOTDir, assemblyName);
                string assetPath = Path.Combine(assetDir, $"{assemblyName}.bytes");
                ValidateCopiedAssembly(
                    sourcePath,
                    assetPath,
                    $"MiniGame AOT 补充元数据 {assemblyName} 缺失或已过期，请执行 HybridCLR/CopyAotDlls 后重新构建 YooAsset");
            }
        }

        private static void ValidateCopiedAssembly(string sourcePath, string assetPath, string errorMessage)
        {
            if (!File.Exists(sourcePath) || !File.Exists(assetPath) || !FilesHaveSameHash(sourcePath, assetPath))
            {
                throw new BuildFailedException(
                    $"{errorMessage}\n源文件：{sourcePath}\n资源文件：{assetPath}");
            }
        }

        private static bool FilesHaveSameHash(string leftPath, string rightPath)
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream leftStream = File.OpenRead(leftPath);
            byte[] leftHash = sha256.ComputeHash(leftStream);
            using FileStream rightStream = File.OpenRead(rightPath);
            byte[] rightHash = sha256.ComputeHash(rightStream);
            return leftHash.SequenceEqual(rightHash);
        }

        private static void ValidateRemoteURL(string settingName, string url, BuildType buildType)
        {
            bool isValid = Uri.TryCreate(url, UriKind.Absolute, out Uri uri);
            bool isLocal = isValid &&
                    (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            bool isHttps = isValid && uri.Scheme == Uri.UriSchemeHttps;
            if (isValid && !isLocal && (buildType != BuildType.Release || isHttps))
            {
                return;
            }

            string message = $"{settingName} 当前为 “{url}”。微信真机必须使用已配置合法域名的 HTTPS CDN。";
            if (buildType == BuildType.Release)
            {
                throw new BuildFailedException(message);
            }

            Debug.LogWarning(message);
        }

        private static void ValidateWebSocketURL(string urlTemplate, BuildType buildType)
        {
            string testUrl;
            try
            {
                testUrl = string.Format(urlTemplate, "127.0.0.1", 30300, 31300);
            }
            catch (FormatException e)
            {
                throw new BuildFailedException(
                    $"GlobalConfig.MiniGameRouterWebSocketUrl 模板无效：{urlTemplate}\n{e.Message}");
            }

            bool isValid = Uri.TryCreate(testUrl, UriKind.Absolute, out Uri uri);
            bool isLocal = isValid &&
                    (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            bool isSecure = isValid && uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
            if (isValid && !isLocal && (buildType != BuildType.Release || isSecure))
            {
                return;
            }

            string message =
                    $"GlobalConfig.MiniGameRouterWebSocketUrl 当前为“{urlTemplate}”。微信真机必须使用已配置合法域名的 WSS 地址。";
            if (buildType == BuildType.Release)
            {
                throw new BuildFailedException(message);
            }

            Debug.LogWarning(message);
        }

        private static void ValidateMiniGameSDKCDN(BuildType buildType)
        {
            UnityEngine.Object config = AssetDatabase.LoadMainAssetAtPath(MiniGameConfigPath);
            if (config == null)
            {
                throw new BuildFailedException($"微信转换配置不存在：{MiniGameConfigPath}");
            }

            SerializedObject serializedConfig = new(config);
            SerializedProperty projectConf = serializedConfig.FindProperty("ProjectConf");
            SerializedProperty assetLoadType = projectConf?.FindPropertyRelative("assetLoadType");
            SerializedProperty cdn = projectConf?.FindPropertyRelative("CDN");
            if (assetLoadType == null || cdn == null)
            {
                throw new BuildFailedException("无法读取微信转换 SDK 的 assetLoadType/CDN 配置。");
            }

            if (assetLoadType.intValue == 0)
            {
                ValidateRemoteURL("MiniGameConfig.ProjectConf.CDN", cdn.stringValue, buildType);
            }
        }
    }

    public sealed class WeChatBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100;

        public void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            if (report.summary.platform.ToString() == "MiniGame" && !IsHybridCLRStrippedAOTBuild(report))
            {
                WeChatBuildTools.ValidateBuild(true);
            }
        }

        private static bool IsHybridCLRStrippedAOTBuild(UnityEditor.Build.Reporting.BuildReport report)
        {
            if (string.IsNullOrWhiteSpace(report.summary.outputPath))
            {
                return false;
            }

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string strippedAOTTempRoot = Path.GetFullPath(
                Path.Combine(projectRoot, "HybridCLRData", "StrippedAOTDllsTempProj"));
            string outputPath = Path.GetFullPath(report.summary.outputPath);

            return outputPath.Equals(strippedAOTTempRoot, StringComparison.OrdinalIgnoreCase) ||
                    outputPath.StartsWith(
                        strippedAOTTempRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
        }
    }
}

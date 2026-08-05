using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

namespace ET
{
    /// <summary>
    /// Project-owned YooAsset builder for WeChat MiniGame.
    /// It intentionally mirrors the options exposed by YooAsset's official builder.
    /// </summary>
    public sealed class WeChatYooAssetBuilderWindow : EditorWindow
    {
        private const float LabelWidth = 170f;

        private string[] packageNames = Array.Empty<string>();
        private List<Type> encryptionTypes = new();
        private string packageName;
        private string packageVersion;
        private EBuildPipeline buildPipeline;
        private Vector2 scrollPosition;

        [MenuItem("ET/WeChat/YooAsset Builder", false, ETMenuItemPriority.BuildTool + 22)]
        public static void OpenWindow()
        {
            WeChatYooAssetBuilderWindow window = GetWindow<WeChatYooAssetBuilderWindow>("WeChat YooAsset Builder");
            window.minSize = new Vector2(620f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            packageNames = AssetBundleCollectorSettingData.Setting.Packages
                    .Select(package => package.PackageName)
                    .ToArray();
            encryptionTypes = EditorTools.GetAssignableTypes(typeof(IEncryptionServices));

            if (packageNames.Length > 0)
            {
                packageName = packageNames.Contains(packageName) ? packageName : packageNames[0];
                buildPipeline = AssetBundleBuilderSetting.GetPackageBuildPipeline(packageName);
            }

            packageVersion = GetDefaultPackageVersion();
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = LabelWidth;
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.Space(8f);

            if (packageNames.Length == 0)
            {
                EditorGUILayout.HelpBox("No YooAsset package was found in AssetBundle Collector settings.", MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawPackageAndPipeline();
            EditorGUILayout.Space(8f);
            DrawBuildOptions();
            EditorGUILayout.Space(12f);
            DrawBuildButton();

            EditorGUILayout.EndScrollView();
        }

        private void DrawPackageAndPipeline()
        {
            int packageIndex = Math.Max(0, Array.IndexOf(packageNames, packageName));
            int newPackageIndex = EditorGUILayout.Popup("Package", packageIndex, packageNames);
            if (newPackageIndex != packageIndex)
            {
                packageName = packageNames[newPackageIndex];
                buildPipeline = AssetBundleBuilderSetting.GetPackageBuildPipeline(packageName);
            }

            EBuildPipeline newPipeline = (EBuildPipeline)EditorGUILayout.EnumPopup("Build Pipeline", buildPipeline);
            if (newPipeline != buildPipeline)
            {
                buildPipeline = newPipeline;
                AssetBundleBuilderSetting.SetPackageBuildPipeline(packageName, buildPipeline);
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.EnumPopup("Build Target", EditorUserBuildSettings.activeBuildTarget);
            }

            if (!WeChatYooAssetBuildService.IsMiniGameTarget)
            {
                EditorGUILayout.HelpBox(
                    "The active build target is not MiniGame. Switch the project to Weixin MiniGame before building.",
                    MessageType.Warning);
            }
        }

        private void DrawBuildOptions()
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Build Output", AssetBundleBuilderHelper.GetDefaultBuildOutputRoot());
            }

            packageVersion = EditorGUILayout.TextField("Build Version", packageVersion);

            EBuildMode buildMode = AssetBundleBuilderSetting.GetPackageBuildMode(packageName, buildPipeline);
            EBuildMode[] supportedModes = WeChatYooAssetBuildService.GetSupportedBuildModes(buildPipeline);
            int modeIndex = Math.Max(0, Array.IndexOf(supportedModes, buildMode));
            EBuildMode newBuildMode = supportedModes[EditorGUILayout.Popup(
                "Build Mode",
                modeIndex,
                supportedModes.Select(mode => mode.ToString()).ToArray())];
            if (newBuildMode != buildMode)
            {
                AssetBundleBuilderSetting.SetPackageBuildMode(packageName, buildPipeline, newBuildMode);
            }

            DrawEncryptionOption();

            if (buildPipeline != EBuildPipeline.RawFileBuildPipeline)
            {
                ECompressOption compressOption = AssetBundleBuilderSetting.GetPackageCompressOption(packageName, buildPipeline);
                ECompressOption newCompressOption = (ECompressOption)EditorGUILayout.EnumPopup("Compression", compressOption);
                if (newCompressOption != compressOption)
                {
                    AssetBundleBuilderSetting.SetPackageCompressOption(packageName, buildPipeline, newCompressOption);
                }
            }

            EFileNameStyle fileNameStyle = AssetBundleBuilderSetting.GetPackageFileNameStyle(packageName, buildPipeline);
            EFileNameStyle newFileNameStyle = (EFileNameStyle)EditorGUILayout.EnumPopup("File Name Style", fileNameStyle);
            if (newFileNameStyle != fileNameStyle)
            {
                AssetBundleBuilderSetting.SetPackageFileNameStyle(packageName, buildPipeline, newFileNameStyle);
            }

            EBuildinFileCopyOption copyOption = AssetBundleBuilderSetting.GetPackageBuildinFileCopyOption(packageName, buildPipeline);
            EBuildinFileCopyOption newCopyOption = (EBuildinFileCopyOption)EditorGUILayout.EnumPopup("Copy Buildin File Option", copyOption);
            if (newCopyOption != copyOption)
            {
                copyOption = newCopyOption;
                AssetBundleBuilderSetting.SetPackageBuildinFileCopyOption(packageName, buildPipeline, copyOption);
            }

            if (copyOption == EBuildinFileCopyOption.ClearAndCopyByTags ||
                copyOption == EBuildinFileCopyOption.OnlyCopyByTags)
            {
                string copyParams = AssetBundleBuilderSetting.GetPackageBuildinFileCopyParams(packageName, buildPipeline);
                string newCopyParams = EditorGUILayout.TextField("Copy Buildin File Param", copyParams);
                if (newCopyParams != copyParams)
                {
                    AssetBundleBuilderSetting.SetPackageBuildinFileCopyParams(packageName, buildPipeline, newCopyParams);
                }
            }
        }

        private void DrawEncryptionOption()
        {
            if (encryptionTypes.Count == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Encryption", "None");
                }
                return;
            }

            string encryptionClassName = AssetBundleBuilderSetting.GetPackageEncyptionClassName(packageName, buildPipeline);
            int encryptionIndex = encryptionTypes.FindIndex(type => type.FullName == encryptionClassName);
            if (encryptionIndex < 0)
            {
                encryptionIndex = 0;
            }

            int newEncryptionIndex = EditorGUILayout.Popup(
                "Encryption",
                encryptionIndex,
                encryptionTypes.Select(type => type.Name).ToArray());
            string newEncryptionClassName = encryptionTypes[newEncryptionIndex].FullName;
            if (newEncryptionClassName != encryptionClassName)
            {
                AssetBundleBuilderSetting.SetPackageEncyptionClassName(packageName, buildPipeline, newEncryptionClassName);
            }
        }

        private void DrawBuildButton()
        {
            bool canBuild = WeChatYooAssetBuildService.IsMiniGameTarget &&
                    !string.IsNullOrWhiteSpace(packageVersion) &&
                    !UnityEditor.BuildPipeline.isBuildingPlayer &&
                    !EditorApplication.isCompiling;

            using (new EditorGUI.DisabledScope(!canBuild))
            {
                if (GUILayout.Button("Build", GUILayout.Height(34f)))
                {
                    EBuildMode buildMode = AssetBundleBuilderSetting.GetPackageBuildMode(packageName, buildPipeline);
                    if (EditorUtility.DisplayDialog(
                            "WeChat YooAsset Build",
                            $"Build package '{packageName}' with mode '{buildMode}'?",
                            "Build",
                            "Cancel"))
                    {
                        string selectedPackage = packageName;
                        string selectedVersion = packageVersion.Trim();
                        EBuildPipeline selectedPipeline = buildPipeline;
                        EditorApplication.delayCall += () => ExecuteBuild(selectedPackage, selectedVersion, selectedPipeline);
                    }
                }
            }
        }

        private static void ExecuteBuild(string selectedPackage, string selectedVersion, EBuildPipeline selectedPipeline)
        {
            try
            {
                EditorTools.ClearUnityConsole();
                WeChatYooAssetBuildOptions options = WeChatYooAssetBuildOptions.FromBuilderSettings(
                    selectedPackage,
                    selectedVersion,
                    selectedPipeline);
                YooAsset.Editor.BuildResult result = WeChatYooAssetBuildService.Build(options);
                EditorUtility.RevealInFinder(result.OutputPackageDirectory);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("WeChat YooAsset Build", exception.Message, "OK");
            }
        }

        private static string GetDefaultPackageVersion()
        {
            int totalMinutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            return DateTime.Now.ToString("yyyy-MM-dd") + "-" + totalMinutes;
        }
    }

    /// <summary>
    /// Serializable input for a future command-line or rotating package build.
    /// The fields match YooAsset's official builder options.
    /// </summary>
    [Serializable]
    public sealed class WeChatYooAssetBuildOptions
    {
        public string PackageName;
        public string PackageVersion;
        public EBuildPipeline BuildPipeline;
        public EBuildMode BuildMode;
        public string EncryptionClassName;
        public ECompressOption CompressOption;
        public EFileNameStyle FileNameStyle;
        public EBuildinFileCopyOption BuildinFileCopyOption;
        public string BuildinFileCopyParams;

        public static WeChatYooAssetBuildOptions FromBuilderSettings(
            string packageName,
            string packageVersion,
            EBuildPipeline buildPipeline)
        {
            return new WeChatYooAssetBuildOptions
            {
                PackageName = packageName,
                PackageVersion = packageVersion,
                BuildPipeline = buildPipeline,
                BuildMode = AssetBundleBuilderSetting.GetPackageBuildMode(packageName, buildPipeline),
                EncryptionClassName = AssetBundleBuilderSetting.GetPackageEncyptionClassName(packageName, buildPipeline),
                CompressOption = AssetBundleBuilderSetting.GetPackageCompressOption(packageName, buildPipeline),
                FileNameStyle = AssetBundleBuilderSetting.GetPackageFileNameStyle(packageName, buildPipeline),
                BuildinFileCopyOption = AssetBundleBuilderSetting.GetPackageBuildinFileCopyOption(packageName, buildPipeline),
                BuildinFileCopyParams = AssetBundleBuilderSetting.GetPackageBuildinFileCopyParams(packageName, buildPipeline),
            };
        }
    }

    public static class WeChatYooAssetBuildService
    {
        public static bool IsMiniGameTarget => EditorUserBuildSettings.activeBuildTarget.ToString() == "MiniGame";

        public static EBuildMode[] GetSupportedBuildModes(EBuildPipeline buildPipeline)
        {
            return buildPipeline switch
            {
                EBuildPipeline.BuiltinBuildPipeline => new[]
                {
                    EBuildMode.ForceRebuild,
                    EBuildMode.IncrementalBuild,
                    EBuildMode.DryRunBuild,
                    EBuildMode.SimulateBuild,
                },
                EBuildPipeline.ScriptableBuildPipeline => new[]
                {
                    EBuildMode.IncrementalBuild,
                    EBuildMode.SimulateBuild,
                },
                EBuildPipeline.RawFileBuildPipeline => new[]
                {
                    EBuildMode.ForceRebuild,
                    EBuildMode.SimulateBuild,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(buildPipeline), buildPipeline, null),
            };
        }

        public static YooAsset.Editor.BuildResult Build(WeChatYooAssetBuildOptions options)
        {
            Validate(options);

            BuildParameters parameters = CreateBuildParameters(options);
            IBuildPipeline pipeline = CreateBuildPipeline(options.BuildPipeline);
            YooAsset.Editor.BuildResult result = pipeline.Run(parameters, true);
            if (!result.Success)
            {
                throw new BuildFailedException(
                    $"YooAsset build failed: task={result.FailedTask}, error={result.ErrorInfo}");
            }

            if (options.BuildMode != EBuildMode.DryRunBuild && options.BuildMode != EBuildMode.SimulateBuild)
            {
                int buildinFileCount = BuildinFileManifestBuilder.Build();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log(
                    $"WeChat YooAsset build completed: package={options.PackageName}, " +
                    $"version={options.PackageVersion}, buildinFiles={buildinFileCount}, " +
                    $"output={result.OutputPackageDirectory}");
            }

            return result;
        }

        private static void Validate(WeChatYooAssetBuildOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!IsMiniGameTarget)
            {
                throw new BuildFailedException(
                    $"Active build target is {EditorUserBuildSettings.activeBuildTarget}. Switch to Weixin MiniGame first.");
            }

            if (string.IsNullOrWhiteSpace(options.PackageName) ||
                !AssetBundleCollectorSettingData.Setting.Packages.Any(package => package.PackageName == options.PackageName))
            {
                throw new BuildFailedException($"YooAsset package was not found: {options.PackageName}");
            }

            if (string.IsNullOrWhiteSpace(options.PackageVersion))
            {
                throw new BuildFailedException("Package version cannot be empty.");
            }

            if (!GetSupportedBuildModes(options.BuildPipeline).Contains(options.BuildMode))
            {
                throw new BuildFailedException(
                    $"Build mode {options.BuildMode} is not supported by {options.BuildPipeline}.");
            }
        }

        private static BuildParameters CreateBuildParameters(WeChatYooAssetBuildOptions options)
        {
            BuildParameters parameters = options.BuildPipeline switch
            {
                EBuildPipeline.BuiltinBuildPipeline => new BuiltinBuildParameters
                {
                    CompressOption = options.CompressOption,
                    DisableWriteTypeTree = false,
                    IgnoreTypeTreeChanges = true,
                },
                EBuildPipeline.ScriptableBuildPipeline => new ScriptableBuildParameters
                {
                    CompressOption = options.CompressOption,
                },
                EBuildPipeline.RawFileBuildPipeline => new RawFileBuildParameters(),
                _ => throw new ArgumentOutOfRangeException(nameof(options.BuildPipeline), options.BuildPipeline, null),
            };

            parameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            parameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            parameters.BuildPipeline = options.BuildPipeline.ToString();
            parameters.BuildTarget = EditorUserBuildSettings.activeBuildTarget;
            parameters.BuildMode = options.BuildMode;
            parameters.PackageName = options.PackageName;
            parameters.PackageVersion = options.PackageVersion;
            parameters.EnableSharePackRule = options.BuildPipeline != EBuildPipeline.RawFileBuildPipeline;
            parameters.VerifyBuildingResult = true;
            parameters.FileNameStyle = options.FileNameStyle;
            parameters.BuildinFileCopyOption = options.BuildinFileCopyOption;
            parameters.BuildinFileCopyParams = options.BuildinFileCopyParams ?? string.Empty;
            parameters.EncryptionServices = CreateEncryptionInstance(options.EncryptionClassName);
            return parameters;
        }

        private static IBuildPipeline CreateBuildPipeline(EBuildPipeline buildPipeline)
        {
            return buildPipeline switch
            {
                EBuildPipeline.BuiltinBuildPipeline => new BuiltinBuildPipeline(),
                EBuildPipeline.ScriptableBuildPipeline => new ScriptableBuildPipeline(),
                EBuildPipeline.RawFileBuildPipeline => new RawFileBuildPipeline(),
                _ => throw new ArgumentOutOfRangeException(nameof(buildPipeline), buildPipeline, null),
            };
        }

        private static IEncryptionServices CreateEncryptionInstance(string encryptionClassName)
        {
            Type encryptionType = EditorTools.GetAssignableTypes(typeof(IEncryptionServices))
                    .FirstOrDefault(type => type.FullName == encryptionClassName);
            return encryptionType == null ? null : (IEncryptionServices)Activator.CreateInstance(encryptionType);
        }
    }
}

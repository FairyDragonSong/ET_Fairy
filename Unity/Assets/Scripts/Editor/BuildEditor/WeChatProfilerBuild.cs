using System;
using System.IO;
using System.Reflection;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace ET
{
    // Shared by the window and Tools/RedBlue/ExportWeChat.csx. No SDK assembly dependency.
    public static class WeChatProfilerBuild
    {
        public static bool Running { get; private set; }
        private const string ConfigPath = "Assets/WX-WASM-SDK-V2/Editor/MiniGameConfig.asset";
        public static string StatusPath => Path.GetFullPath("../Temp/wechat-build-status.txt");

        public static void Queue(string output, bool development, bool prepare, bool requireDemo = false,
            bool? simd = null, bool? threads = null, string cpuVariant = null, string resultUrl = null)
        {
            if (Running || EditorApplication.isPlaying || EditorApplication.isCompiling)
                throw new InvalidOperationException("Stop Play and wait for compilation/build completion first.");
            if (EditorUserBuildSettings.activeBuildTarget.ToString() != "MiniGame")
                throw new InvalidOperationException("Switch to MiniGame first.");
            if (requireDemo && !Resources.Load<GlobalConfig>("GlobalConfig").EnableRedBlueDemo)
                throw new InvalidOperationException("Enable RedBlue demo first.");
            string project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string destination = Path.GetFullPath(Path.Combine(project, output));
            if (Path.IsPathRooted(output) || !destination.StartsWith(project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output must be a directory inside the Unity project.");
            Running = true;
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
            File.WriteAllText(StatusPath, "queued");
            EditorApplication.CallbackFunction build = null;
            build = () =>
            {
                EditorApplication.update -= build;
                Export(output, destination, development, prepare, simd, threads, cpuVariant, resultUrl);
            };
            EditorApplication.update += build;
        }

        private static void Export(string output, string destination, bool development, bool prepare,
            bool? simd, bool? threads, string cpuVariant, string resultUrl)
        {
            var config = AssetDatabase.LoadMainAssetAtPath(ConfigPath);
            string original = config == null ? null : EditorJsonUtility.ToJson(config);
            bool oldDevelopment = EditorUserBuildSettings.development;
            bool oldConnect = EditorUserBuildSettings.connectProfiler;
            bool oldDeep = EditorUserBuildSettings.buildWithDeepProfilingSupport;
            var target = NamedBuildTarget.WeixinMiniGame;
            var oldGeneration = PlayerSettings.GetIl2CppCodeGeneration(target);
            bool oldSimd = PlayerSettings.MiniGame.simdSupport, oldThreads = PlayerSettings.MiniGame.threadsSupport;
            bool oldRenderThread = PlayerSettings.GetRenderThread(target);
            const string experimentPath = "Assets/Resources/RedBlueCpuExperiment.json";
            string oldExperiment = File.Exists(experimentPath) ? File.ReadAllText(experimentPath) : null;
            string experimentId = null;
            try
            {
                if (config == null) throw new InvalidOperationException("Missing WeChat SDK config.");
                var settings = new SerializedObject(config);
                Set(settings, "ProjectConf.relativeDST", output);
                Set(settings, "ProjectConf.DST", destination);
                Set(settings, "CompileOptions.DevelopBuild", development);
                Set(settings, "CompileOptions.AutoProfile", development);
                Set(settings, "CompileOptions.Il2CppOptimizeSize", false);
                Set(settings, "CompileOptions.enableRenderThread", threads ?? false);
                settings.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(config);
                AssetDatabase.SaveAssets();
                EditorUserBuildSettings.development = development;
                EditorUserBuildSettings.connectProfiler = development;
                EditorUserBuildSettings.buildWithDeepProfilingSupport = false;
                PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSpeed);
                if (simd.HasValue) PlayerSettings.MiniGame.simdSupport = simd.Value;
                PlayerSettings.MiniGame.threadsSupport = threads ?? false;
                PlayerSettings.SetRenderThread(target, threads ?? false);
                if (BuildPipeline.IsMiniGameBuildThreadedFromPlayerSettings() != (threads ?? false))
                    throw new Exception("Effective MiniGame thread configuration differs from requested experiment.");
                if (cpuVariant != null)
                {
                    experimentId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + cpuVariant;
                    File.WriteAllText(experimentPath, JsonUtility.ToJson(new RedBlue.RedBlueCpuExperiment
                    {
                        variant = cpuVariant, buildId = experimentId, simd = simd ?? oldSimd, threads = threads ?? false,
                        allowMiniGameParallel = threads ?? false, comparePacked = cpuVariant == "packed-ab" || cpuVariant == "logic-deadline",
                        preferPacked = cpuVariant == "logic-active-cells" || cpuVariant == "logic-deadline" || cpuVariant == "packed-split" || cpuVariant == "packed-split-all", resultUrl = resultUrl ?? "",
                        comparePackedBounded = cpuVariant == "packed-bounded", preferPackedBounded = cpuVariant == "packed-bounded",
                        compareSplit = cpuVariant == "packed-split" || cpuVariant == "packed-split-all",
                        splitSimulationTicks = cpuVariant == "packed-split" || cpuVariant == "packed-split-all",
                        compareAllFormations = cpuVariant == "packed-split-all"
                    }, true));
                    AssetDatabase.ImportAsset(experimentPath, ImportAssetOptions.ForceSynchronousImport);
                }
                if (prepare)
                {
                    File.WriteAllText(StatusPath, "compiling ET and generating matching HybridCLR AOT dependencies");
                    AssemblyTool.DoCompile(development ? BuildType.Debug : BuildType.Release);
                    if (EditorUtility.scriptCompilationFailed) throw new Exception("ET compilation failed.");
                    if (EditorUserBuildSettings.development != development)
                        throw new BuildFailedException("ET compilation changed the requested Development setting.");
                    PrebuildCommand.GenerateAll();
                }
                bool bridgeDevelopment = ReadMethodBridgeDevelopment();
                if (bridgeDevelopment != development || EditorUserBuildSettings.development != development)
                    throw new BuildFailedException("HybridCLR/ET mode differs from the requested build. Regenerate with Prepare enabled.");
                File.WriteAllText(StatusPath, "building resources");
                HybridCLREditor.CopyAotDll();
                WeChatBuildTools.BuildYooAssetPackage();
                WeChatBuildTools.ValidateBuild(true);
                // BuildPlayer / YooAsset may unload the asset's native object. Reload it.
                config = AssetDatabase.LoadMainAssetAtPath(ConfigPath);
                File.WriteAllText(StatusPath, "exporting " + (development ? "Development + Autoconnect Profiler" : "Release"));
                Type core = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if ((core = assembly.GetType("WeChatWASM.WXConvertCore")) != null) break;
                if (core == null) throw new Exception("WeChat SDK not loaded.");
                var export = core.GetMethod("DoExport", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                for (int pass = 0; ; pass++)
                {
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    object result = null;
                    for (int attempt = 0; ; attempt++)
                    {
                        try { result = export.Invoke(null, new object[] { attempt == 0 }); break; }
                        catch (TargetInvocationException error) when (attempt < 2 &&
                            error.InnerException is IOException io &&
                            ((io.HResult & 0xffff) == 32 || (io.HResult & 0xffff) == 33) &&
                            io.Message.Contains(Path.Combine("slim", "backup.txt")))
                        {
                            // SDK failed after BuildPlayer; retry conversion without rebuilding Wasm.
                            File.WriteAllText(StatusPath, "retrying SDK conversion after a temporary backup.txt sharing violation");
                            GC.Collect(); GC.WaitForPendingFinalizers();
                            System.Threading.Thread.Sleep(500);
                        }
                    }
                    if (result.ToString() != "SUCCEED") throw new Exception("WeChat export result=" + result);
                    try { WeChatBuildTools.ValidateBuild(true); break; }
                    catch (BuildFailedException error) when (pass == 0 && error.Message.Contains("MiniGame AOT"))
                    {
                        // Full export can update stripped Unity.Loader after GenerateAll.
                        // Refresh AOT resources AND rebuild the embedded built-in manifest.
                        File.WriteAllText(StatusPath, "synchronizing final stripped AOT; rebuilding resources and exporting a second pass");
                        HybridCLREditor.CopyAotDll();
                        WeChatBuildTools.BuildYooAssetPackage();
                        WeChatBuildTools.ValidateBuild(true);
                    }
                }
                WeChatBuildTools.PublishYooAssetToLocalCDN();
                if (ReadMethodBridgeDevelopment() != development)
                    throw new BuildFailedException("HybridCLR Development marker changed during export.");
                if (simd.HasValue && PlayerSettings.MiniGame.simdSupport != simd.Value)
                    throw new Exception("SIMD setting changed during export; reject mislabeled experiment.");
                if (threads.HasValue && PlayerSettings.MiniGame.threadsSupport != threads.Value)
                    throw new Exception("Thread setting changed during export; reject mislabeled experiment.");
                if (threads.HasValue && BuildPipeline.IsMiniGameBuildThreadedFromPlayerSettings() != threads.Value)
                    throw new Exception("Effective thread setting changed during export.");
                string evidence = Path.Combine(destination, "profiler-build-evidence");
                Directory.CreateDirectory(evidence);
                foreach (string source in new[] { "Assets/Scripts/Loader/RedBlue/BurstCrowdKernel.cs", "Assets/Scripts/Loader/RedBlue/RedBlueCpuBenchmark.cs", "Assets/Scripts/Loader/RedBlue/RedBlueFrameBenchmark.cs", "Assets/Scripts/Loader/RedBlue/RedBlueDemo.cs", "Assets/Scripts/Loader/RedBlue/CrowdSimulation.cs", "Assets/Scripts/Loader/RedBlue/CrowdStepScheduler.cs", "Assets/Scripts/Editor/BuildEditor/WeChatProfilerBuild.cs", "Assets/Scripts/Editor/Assembly/AssemblyTool.cs", "Assets/Resources/RedBlueCpuExperiment.json", "ProjectSettings/BurstAotSettings_MiniGame.json", "Temp/BurstOutput/Plugins/lib_burst_generated.a", HybridCLR.Editor.SettingsUtil.GeneratedCppDir + "/MethodBridge.cpp" })
                    if (File.Exists(source)) File.Copy(source, Path.Combine(evidence, Path.GetFileName(source)), true);
                config = AssetDatabase.LoadMainAssetAtPath(ConfigPath);
                string version = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "Bundles/DefaultPackage/PackageManifest_DefaultPackage.version")).Trim();
                var manifest = new BuildManifest
                {
                    buildId = experimentId ?? DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"), utc = DateTime.UtcNow.ToString("o"),
                    unity = Application.unityVersion, development = development, autoConnect = development,
                    deepProfiling = false, output = destination, resourceVersion = version,
                    productVersion = Application.version, gitRevision = ReadGitRevision(projectRoot: Path.GetFullPath("..")),
                    simd = PlayerSettings.MiniGame.simdSupport, threads = PlayerSettings.MiniGame.threadsSupport, cpuVariant = cpuVariant,
                    hybridClrMethodBridgeDevelopment = ReadMethodBridgeDevelopment(), etCompilationRegenerated = prepare,
                    kernelSourceSha256 = Sha256("Assets/Scripts/Loader/RedBlue/BurstCrowdKernel.cs"),
                    benchmarkSourceSha256 = Sha256("Assets/Scripts/Loader/RedBlue/RedBlueCpuBenchmark.cs"),
                    sdkConfig = EditorJsonUtility.ToJson(config),
                    burstAotSettingsJson = File.Exists("ProjectSettings/BurstAotSettings_MiniGame.json")
                        ? File.ReadAllText("ProjectSettings/BurstAotSettings_MiniGame.json") : "missing; platform defaults apply",
                    note = "Engine-generated " + (development ? "Development" : "Release") + " Wasm; no additional compatibility rewrite. Profile and release builds must be measured separately."
                };
                File.WriteAllText(Path.Combine(destination, "profiler-build.json"), JsonUtility.ToJson(manifest, true));
                File.WriteAllText(StatusPath, "succeeded\noutput=" + destination + "\nmanifest=profiler-build.json");
                Debug.Log("WECHAT_BUILD_OK: " + destination);
            }
            catch (Exception e)
            {
                File.WriteAllText(StatusPath, "failed\n" + e);
                Debug.LogException(e);
            }
            finally
            {
                try
                {
                    config = AssetDatabase.LoadMainAssetAtPath(ConfigPath);
                    if (original != null && config != null)
                    { EditorJsonUtility.FromJsonOverwrite(original, config); EditorUtility.SetDirty(config); }
                }
                finally
                {
                    EditorUserBuildSettings.development = oldDevelopment;
                    EditorUserBuildSettings.connectProfiler = oldConnect;
                    EditorUserBuildSettings.buildWithDeepProfilingSupport = oldDeep;
                    PlayerSettings.SetIl2CppCodeGeneration(target, oldGeneration);
                    PlayerSettings.MiniGame.simdSupport = oldSimd; PlayerSettings.MiniGame.threadsSupport = oldThreads;
                    PlayerSettings.SetRenderThread(target, oldRenderThread);
                    if (cpuVariant != null)
                    {
                        if (oldExperiment != null) File.WriteAllText(experimentPath, oldExperiment);
                        else AssetDatabase.DeleteAsset(experimentPath);
                    }
                    AssetDatabase.SaveAssets();
                    Running = false;
                }
            }
        }

        private static void Set(SerializedObject settings, string name, object value)
        {
            var property = settings.FindProperty(name) ?? throw new InvalidOperationException("SDK field missing: " + name);
            if (value is bool boolean) property.boolValue = boolean;
            else property.stringValue = (string)value;
        }
        private static bool ReadMethodBridgeDevelopment()
        {
            string file = HybridCLR.Editor.SettingsUtil.GeneratedCppDir + "/MethodBridge.cpp";
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(file), @"// DEVELOPMENT=([01])");
            if (!match.Success) throw new BuildFailedException("Missing HybridCLR MethodBridge Development marker.");
            return match.Groups[1].Value == "1";
        }
        private static string Sha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
        }

        private static string ReadGitRevision(string projectRoot)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
                { WorkingDirectory = projectRoot, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var process = System.Diagnostics.Process.Start(start);
                string revision = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
                return revision + " (working tree may contain local changes; archive source with build)";
            }
            catch { return "unavailable"; }
        }

        [Serializable] private sealed class BuildManifest
        {
            public string buildId, utc, unity, output, resourceVersion, productVersion, gitRevision, sdkConfig, burstAotSettingsJson, note, cpuVariant, kernelSourceSha256, benchmarkSourceSha256;
            public bool development, autoConnect, deepProfiling, simd, threads, hybridClrMethodBridgeDevelopment, etCompilationRegenerated;
        }
    }
}

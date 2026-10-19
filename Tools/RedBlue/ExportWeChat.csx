// Execute as a method body through Unity MCP after switching to MiniGame,
// ET/Compile and HybridCLR/Generate/All. Local validation export, no upload.
// Optional AppDomain data: RedBlue.Export.Output (project-relative directory),
// RedBlue.Export.Prepare=true (also compile ET and generate HybridCLR dependencies).
if (UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isCompiling)
    throw new System.InvalidOperationException("Stop Play and wait for compilation first.");
if (UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString() != "MiniGame")
    throw new System.InvalidOperationException("Switch to MiniGame first.");
if (!UnityEngine.Resources.Load<ET.GlobalConfig>("GlobalConfig").EnableRedBlueDemo)
    throw new System.InvalidOperationException("Enable RedBlue demo first.");
UnityEditor.EditorApplication.CallbackFunction build = null;
build = () =>
{
    UnityEditor.EditorApplication.update -= build;
    string status = System.IO.Path.GetFullPath("../Temp/redblue-wechat-export.txt");
    var config = WeChatWASM.WXConvertCore.config;
    string original = UnityEditor.EditorJsonUtility.ToJson(config);
    var originalGeneration = UnityEditor.PlayerSettings.GetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.WeixinMiniGame);
    bool originalDevelopment = UnityEditor.EditorUserBuildSettings.development;
    try
    {
        string output = System.AppDomain.CurrentDomain.GetData("RedBlue.Export.Output") as string ?? "BuildArtifacts/RedBlue-WeChat";
        string projectRoot = System.IO.Path.GetFullPath(UnityEngine.Application.dataPath + "/..");
        string outputRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, output));
        if (System.IO.Path.IsPathRooted(output) || !outputRoot.StartsWith(projectRoot + System.IO.Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase))
            throw new System.InvalidOperationException("Export output must stay inside the Unity project.");
        config.ProjectConf.relativeDST = output;
        config.ProjectConf.DST = outputRoot;
        config.CompileOptions.DevelopBuild = false;
        config.CompileOptions.AutoProfile = false;
        config.CompileOptions.Il2CppOptimizeSize = false;
        config.CompileOptions.enableRenderThread = false;
        if (object.Equals(System.AppDomain.CurrentDomain.GetData("RedBlue.Export.Prepare"), true))
        {
            System.IO.File.WriteAllText(status, "compiling ET hot-update DLLs");
            ET.AssemblyTool.DoCompile();
            if (UnityEditor.EditorUtility.scriptCompilationFailed) throw new System.Exception("ET compilation failed.");
            System.IO.File.WriteAllText(status, "generating HybridCLR AOT dependencies");
            UnityEditor.EditorUserBuildSettings.development = false;
            UnityEditor.PlayerSettings.SetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.WeixinMiniGame, UnityEditor.Build.Il2CppCodeGeneration.OptimizeSpeed);
            HybridCLR.Editor.Commands.PrebuildCommand.GenerateAll();
        }
        System.IO.File.WriteAllText(status, "building resources");
        ET.HybridCLREditor.CopyAotDll();
        ET.WeChatBuildTools.BuildYooAssetPackage();
        typeof(ET.WeChatBuildTools).GetMethod("ValidateBuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { true });
        UnityEditor.EditorUtility.SetDirty(config);
        UnityEditor.AssetDatabase.SaveAssets();
        System.IO.File.WriteAllText(status, "exporting IL2CPP/Wasm (speed optimized, workers disabled)");
        // Repeated SDK exports can leave the previous slim backup open until finalization.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        var result = WeChatWASM.WXConvertCore.DoExport(true);
        if (result != WeChatWASM.WXConvertCore.WXExportError.SUCCEED) throw new System.Exception("WeChat export result=" + result);
        System.IO.File.WriteAllText(status, "export succeeded; validating generated AOT/resources");
        typeof(ET.WeChatBuildTools).GetMethod("ValidateBuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { true });
        ET.WeChatBuildTools.PublishYooAssetToLocalCDN();
        System.IO.File.WriteAllText(status, "export result=" + result + "\npostBuildValidation=passed\noutput=" + config.ProjectConf.DST + "\nCDN=" + config.ProjectConf.CDN);
    }
    catch (System.Exception error)
    {
        System.IO.File.WriteAllText(status, error.ToString());
        UnityEngine.Debug.LogException(error);
    }
    finally
    {
        UnityEditor.EditorJsonUtility.FromJsonOverwrite(original, config);
        UnityEditor.PlayerSettings.SetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.WeixinMiniGame, originalGeneration);
        UnityEditor.EditorUserBuildSettings.development = originalDevelopment;
        UnityEditor.EditorUtility.SetDirty(config);
        UnityEditor.AssetDatabase.SaveAssets();
    }
};
UnityEditor.EditorApplication.update += build;
return "Export queued; status: ../Temp/redblue-wechat-export.txt";

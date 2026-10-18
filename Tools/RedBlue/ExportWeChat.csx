// Execute as a method body through Unity MCP after switching to MiniGame,
// ET/Compile and HybridCLR/Generate/All. Local validation export, no upload.
if (UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isCompiling)
    throw new System.InvalidOperationException("Stop Play and wait for compilation first.");
if (UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString() != "MiniGame")
    throw new System.InvalidOperationException("Switch to MiniGame first.");
if (!UnityEngine.Resources.Load<ET.GlobalConfig>("GlobalConfig").EnableRedBlueDemo)
    throw new System.InvalidOperationException("Enable RedBlue demo first.");
UnityEditor.EditorApplication.delayCall += () =>
{
    string status = System.IO.Path.GetFullPath("../Temp/redblue-wechat-export.txt");
    var config = WeChatWASM.WXConvertCore.config;
    string original = UnityEditor.EditorJsonUtility.ToJson(config);
    var originalGeneration = UnityEditor.PlayerSettings.GetIl2CppCodeGeneration(UnityEditor.Build.NamedBuildTarget.WeixinMiniGame);
    try
    {
        System.IO.File.WriteAllText(status, "building resources");
        ET.HybridCLREditor.CopyAotDll();
        ET.WeChatBuildTools.BuildYooAssetPackage();
        typeof(ET.WeChatBuildTools).GetMethod("ValidateBuild", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Invoke(null, new object[] { true });
        config.ProjectConf.relativeDST = "BuildArtifacts/RedBlue-WeChat";
        config.ProjectConf.DST = System.IO.Path.GetFullPath(config.ProjectConf.relativeDST);
        config.CompileOptions.DevelopBuild = false;
        config.CompileOptions.AutoProfile = false;
        config.CompileOptions.Il2CppOptimizeSize = false;
        config.CompileOptions.enableRenderThread = false;
        UnityEditor.EditorUtility.SetDirty(config);
        UnityEditor.AssetDatabase.SaveAssets();
        System.IO.File.WriteAllText(status, "exporting IL2CPP/Wasm (speed optimized, workers disabled)");
        var result = WeChatWASM.WXConvertCore.DoExport(true);
        System.IO.File.WriteAllText(status, "export result=" + result + "\noutput=" + config.ProjectConf.DST);
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
        UnityEditor.EditorUtility.SetDirty(config);
        UnityEditor.AssetDatabase.SaveAssets();
    }
};
return "Export queued; status: ../Temp/redblue-wechat-export.txt";

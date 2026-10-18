namespace ET
{
    public static class Define
    {
        /// <summary>
        /// 编辑器下加载热更dll的目录
        /// </summary>
        public const string CodeDir = "Assets/Bundles/Code";

        /// <summary>
        /// F6 和 HybridCLR 共用的持久化热更 DLL 输出目录。
        /// </summary>
        public const string BuildOutputDir = "BuildArtifacts/HotUpdateDlls";

#if DEBUG
        public static bool IsDebug = true;
#else
        public static bool IsDebug = false;
#endif

#if UNITY_EDITOR && !ASYNC
        public static bool IsAsync = false;
#else
        public static bool IsAsync = true;
#endif

#if UNITY_EDITOR
        public static bool IsEditor = true;
#else
        public static bool IsEditor = false;
#endif

#if ENABLE_VIEW
        public static bool EnableView = true;
#else
        public static bool EnableView = false;
#endif

#if ENABLE_IL2CPP
        public static bool EnableIL2CPP = true;
#else
        public static bool EnableIL2CPP = false;
#endif
    }
}

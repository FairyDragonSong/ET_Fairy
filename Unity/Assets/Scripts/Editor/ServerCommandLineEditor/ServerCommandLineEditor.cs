using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ET
{
    public enum DevelopMode
    {
        正式 = 0,
        开发 = 1,
        压测 = 2,
    }

    public class ServerCommandLineEditor: EditorWindow
    {
        [MenuItem("ET/ServerTools", false, ETMenuItemPriority.ServerTools)]
        public static void ShowWindow()
        {
            GetWindow<ServerCommandLineEditor>(DockDefine.Types);
        }

        private int selectStartConfigIndex = 1;
        private string[] startConfigs;
        private string startConfig;
        private DevelopMode developMode;
        private bool routerWebSocket;
        private int routerWebSocketPortOffset = 1000;
        private bool accountAutoRegister;

        public void OnEnable()
        {
            DirectoryInfo directoryInfo = new DirectoryInfo("../Config/Excel/cs/StartConfig");
            this.startConfigs = directoryInfo.GetDirectories().Select(x => x.Name).ToArray();
            this.routerWebSocket = EditorPrefs.GetBool("ET.ServerTools.RouterWebSocket", false);
            this.routerWebSocketPortOffset =
                    EditorPrefs.GetInt("ET.ServerTools.RouterWebSocketPortOffset", 1000);
            this.accountAutoRegister = EditorPrefs.GetBool("ET.ServerTools.AccountAutoRegister", false);
        }

        public void OnGUI()
        {
            selectStartConfigIndex = EditorGUILayout.Popup(selectStartConfigIndex, this.startConfigs);
            this.startConfig = this.startConfigs[this.selectStartConfigIndex];
            this.developMode = (DevelopMode)EditorGUILayout.EnumPopup("起服模式：", this.developMode);
            this.routerWebSocket = EditorGUILayout.Toggle("Router WebSocket：", this.routerWebSocket);
            this.accountAutoRegister = EditorGUILayout.Toggle("账号首次登录自动注册：", this.accountAutoRegister);

            using (new EditorGUI.DisabledScope(!this.routerWebSocket))
            {
                this.routerWebSocketPortOffset =
                        EditorGUILayout.IntField("WebSocket 端口偏移：", this.routerWebSocketPortOffset);
            }

            EditorPrefs.SetBool("ET.ServerTools.RouterWebSocket", this.routerWebSocket);
            EditorPrefs.SetInt(
                "ET.ServerTools.RouterWebSocketPortOffset",
                this.routerWebSocketPortOffset);
            EditorPrefs.SetBool("ET.ServerTools.AccountAutoRegister", this.accountAutoRegister);

            string dotnet = "dotnet.exe";

#if UNITY_EDITOR_OSX
            dotnet = "dotnet";
#endif

            if (GUILayout.Button("Start Server(Single Process)"))
            {
                string arguments =
                        $"App.dll --Process=1 --AppType=Server" +
                        $" --StartConfig=StartConfig/{this.startConfig}" +
                        $" --Develop={(int)this.developMode}" +
                        $" --Console=1" +
                        $" --RouterWebSocket={(this.routerWebSocket ? 1 : 0)}" +
                        $" --RouterWebSocketPortOffset={this.routerWebSocketPortOffset}" +
                        $" --AccountAutoRegister={(this.accountAutoRegister ? 1 : 0)}";
                ProcessHelper.Run(dotnet, arguments, "../Bin/");
            }

            if (GUILayout.Button("Start Watcher"))
            {
                string arguments =
                        $"App.dll --AppType=Watcher" +
                        $" --StartConfig=StartConfig/{this.startConfig}" +
                        $" --Develop={(int)this.developMode}" +
                        $" --Console=1" +
                        $" --RouterWebSocket={(this.routerWebSocket ? 1 : 0)}" +
                        $" --RouterWebSocketPortOffset={this.routerWebSocketPortOffset}" +
                        $" --AccountAutoRegister={(this.accountAutoRegister ? 1 : 0)}";
                ProcessHelper.Run(dotnet, arguments, "../Bin/");
            }

            if (GUILayout.Button("Start Mongo"))
            {
                ProcessHelper.Run("mongod", @"--dbpath=db", "../Database/bin/");
            }
        }
    }
}

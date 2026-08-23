using CommandLine;
using System;
using System.Collections.Generic;

namespace ET
{
    public enum AppType
    {
        Server,
        Watcher, // 每台物理机一个守护进程，用来启动该物理机上的所有进程
        GameTool,
        ExcelExporter,
        Proto2CS,
        BenchmarkClient,
        BenchmarkServer,
        
        Demo,
        LockStep,
    }
    
    public class Options: Singleton<Options>
    {
        [Option("AppType", Required = false, Default = AppType.Server, HelpText = "AppType enum")]
        public AppType AppType { get; set; }

        [Option("StartConfig", Required = false, Default = "StartConfig/Localhost")]
        public string StartConfig { get; set; }

        [Option("Process", Required = false, Default = 1)]
        public int Process { get; set; }
        
        [Option("Develop", Required = false, Default = 0, HelpText = "develop mode, 0正式 1开发 2压测")]
        public int Develop { get; set; }

        [Option("LogLevel", Required = false, Default = 0)]
        public int LogLevel { get; set; }
        
        [Option("Console", Required = false, Default = 0)]
        public int Console { get; set; }

        [Option("RouterWebSocket", Required = false, Default = 0, HelpText = "enable Router WebSocket listener, 0 off 1 on")]
        public int RouterWebSocket { get; set; }

        [Option("RouterWebSocketPortOffset", Required = false, Default = 1000)]
        public int RouterWebSocketPortOffset { get; set; }

        [Option("AccountAutoRegister", Required = false, Default = 0, HelpText = "allow password account creation on first login, 0 off 1 on")]
        public int AccountAutoRegister { get; set; }

        [Option("LoginIpLimit", Required = false, Default = 30, HelpText = "max login requests per IP in one login rate window")]
        public int LoginIpLimit { get; set; }

        [Option("LoginAccountLimit", Required = false, Default = 10, HelpText = "max login requests per account in one login rate window")]
        public int LoginAccountLimit { get; set; }

        [Option("LoginRateWindowSeconds", Required = false, Default = 60, HelpText = "login rate limit fixed window in seconds")]
        public int LoginRateWindowSeconds { get; set; }

        [Option("OuterMessageLimit", Required = false, Default = 100, HelpText = "max outer messages per session per second")]
        public int OuterMessageLimit { get; set; }

        [Option("OuterBytesLimit", Required = false, Default = 262144, HelpText = "max outer bytes per session per second")]
        public int OuterBytesLimit { get; set; }

        [Option("ShutdownTimeoutSeconds", Required = false, Default = 15, HelpText = "maximum graceful shutdown drain time in seconds")]
        public int ShutdownTimeoutSeconds { get; set; }
    }
}

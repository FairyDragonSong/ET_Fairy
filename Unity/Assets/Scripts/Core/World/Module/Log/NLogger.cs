using System;
using NLog;

namespace ET
{
    public class NLogger: ILog
    {
        private readonly NLog.Logger logger;

        static NLogger()
        {
            string configFile = "../Config/NLog/NLog.config";
#if DOTNET
            string profile = Environment.GetEnvironmentVariable("ET_LOG_PROFILE");
            if (profile == "bounded") configFile = "../Config/NLog/NLog.Bounded.config";
            else if (!string.IsNullOrEmpty(profile) && profile != "legacy")
                throw new InvalidOperationException("Unknown ET_LOG_PROFILE; use legacy or bounded");
#endif
            LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(configFile);
            LogManager.Configuration.Variables["currentDir"] = Environment.CurrentDirectory;
#if DOTNET
            string configuredLogRoot = Environment.GetEnvironmentVariable("ET_LOG_ROOT");
            if (!string.IsNullOrWhiteSpace(configuredLogRoot))
            {
                if (!System.IO.Path.IsPathFullyQualified(configuredLogRoot))
                    throw new InvalidOperationException("ET_LOG_ROOT must be an absolute path");
                configuredLogRoot = System.IO.Path.Combine(System.IO.Path.GetFullPath(configuredLogRoot), Environment.ProcessId.ToString());
                System.IO.Directory.CreateDirectory(configuredLogRoot);
                LogManager.Configuration.Variables["logRoot"] = configuredLogRoot;
            }
#endif
        }

        public NLogger(string name, int process, int fiber)
        {
            this.logger = LogManager.GetLogger($"{(uint)process:000000}.{(uint)fiber:0000000000}.{name}");
        }

        public void Trace(string message)
        {
            this.logger.Trace(message);
        }

        public void Warning(string message)
        {
            this.logger.Warn(message);
        }

        public void Info(string message)
        {
            this.logger.Info(message);
        }

        public void Debug(string message)
        {
            this.logger.Debug(message);
        }

        public void Error(string message)
        {
            this.logger.Error(message);
        }

        public void Error(Exception e)
        {
            this.logger.Error(e.ToString());
        }

#if DOTNET
        public void Trace(ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler message)
        {
            this.logger.Trace(message.ToStringAndClear());
        }

        public void Warning(ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler message)
        {
            this.logger.Warn(message.ToStringAndClear());
        }

        public void Info(ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler message)
        {
            this.logger.Info(message.ToStringAndClear());
        }

        public void Debug(ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler message)
        {
            this.logger.Debug(message.ToStringAndClear());
        }

        public void Error(ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler message)
        {
            this.logger.Error(message.ToStringAndClear());
        }
#endif
    }
}

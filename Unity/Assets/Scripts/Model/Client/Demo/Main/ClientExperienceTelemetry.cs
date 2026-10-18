using System;
using System.Globalization;

namespace ET.Client
{
    public enum ClientExperienceStage
    {
        Login,
        EnterMap,
    }

    public static class ClientExperienceTelemetry
    {
        // 记录客户端阶段从开始到结束的耗时。
        public static void RecordSince(ClientExperienceStage stage, long started, bool succeeded)
        {
            double milliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                    System.Diagnostics.Stopwatch.Frequency;
            Log.Info(Format(stage, succeeded, milliseconds));
        }

        // 把客户端体验指标格式化为统一日志字段。
        public static string Format(ClientExperienceStage stage, bool succeeded, double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(milliseconds));
            }

            string name = stage switch
            {
                ClientExperienceStage.Login => "login",
                ClientExperienceStage.EnterMap => "enter_map",
                _ => throw new ArgumentOutOfRangeException(nameof(stage)),
            };
            return $"CLIENT_EXPERIENCE stage={name} outcome={(succeeded ? "success" : "failure")} duration_ms={milliseconds.ToString("F3", CultureInfo.InvariantCulture)}";
        }
    }
}

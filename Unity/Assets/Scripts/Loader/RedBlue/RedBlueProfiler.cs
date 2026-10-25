using System;

namespace ET.RedBlue
{
    /// <summary>用值类型作用域配对 Profiler 采样；纯 .NET 测试下保留接口但不调用 Unity。</summary>
    internal static class RedBlueProfiler
    {
        /// <summary>开始具名采样，配合 using 在当前作用域结束时关闭。</summary>
        public static Scope Measure(string name) => new Scope(name);

        /// <summary>不分配托管对象的采样句柄，确保正常返回或异常退出都能结束采样。</summary>
        internal readonly struct Scope : IDisposable
        {
            /// <summary>在 Unity 环境中记录采样开始。</summary>
            public Scope(string name)
            {
#if UNITY_5_3_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample(name);
#endif
            }

            /// <summary>在 Unity 环境中结束对应采样。</summary>
            public void Dispose()
            {
#if UNITY_5_3_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#endif
            }
        }
    }
}

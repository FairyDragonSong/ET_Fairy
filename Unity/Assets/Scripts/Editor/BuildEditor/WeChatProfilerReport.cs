using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditorInternal;

namespace ET
{
    public static class WeChatProfilerReport
    {
        // Works with any Unity Profiler recording, without RedBlue components or Play mode.
        public static string AnalyzeLoaded(string directory)
        {
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < first) throw new InvalidOperationException("No Profiler frames to analyze.");
            var threads = new Dictionary<string, ThreadData>();
            for (int frame = first; frame <= last; frame++)
            for (int index = 0; index < 512; index++)
            using (var view = ProfilerDriver.GetRawFrameDataView(frame, index))
            {
                if (!view.valid) break;
                string key = view.threadGroupName + "/" + view.threadName + "/" + view.threadId;
                if (!threads.TryGetValue(key, out var data))
                    threads[key] = data = new ThreadData { name = view.threadName, group = view.threadGroupName };
                data.frames.Add(new Frame { index = frame, ms = view.frameTimeMs,
                    engineReportedGpuMs = view.frameGpuTimeMs > 0 ? (double?)view.frameGpuTimeMs : null });
                for (int sample = 0; sample < view.sampleCount; sample++)
                {
                    string name = view.GetSampleName(sample) ?? "(unnamed)";
                    if (!data.markers.TryGetValue(name, out var marker)) data.markers[name] = marker = new Marker { name = name };
                    double inclusive = view.GetSampleTimeMs(sample), self = inclusive;
                    int end = sample + view.GetSampleChildrenCountRecursive(sample);
                    for (int child = sample + 1; child <= end; child += 1 + view.GetSampleChildrenCountRecursive(child))
                        self -= view.GetSampleTimeMs(child);
                    marker.inclusiveMs += inclusive; marker.selfMs += Math.Max(0, self); marker.calls++;
                }
            }
            var rows = threads.Values.Select(t => new
            {
                t.name, t.group, frameCount = t.frames.Count, frameTimeMs = Summarize(t.frames.Select(f => f.ms)),
                engineReportedGpuMs = Summarize(t.frames.Where(f => f.engineReportedGpuMs.HasValue).Select(f => f.engineReportedGpuMs.Value)),
                frames = t.frames,
                markers = t.markers.Values.OrderByDescending(m => m.selfMs).Select(m => new
                {
                    m.name, inclusiveMsPerFrame = m.inclusiveMs / t.frames.Count,
                    selfMsPerFrame = m.selfMs / t.frames.Count, callsPerFrame = (double)m.calls / t.frames.Count
                }).ToArray()
            }).ToArray();
            Directory.CreateDirectory(directory);
            const string caveat = "CPU frame time includes waits. Inclusive marker times overlap and must not be added. " +
                "GPU values are engine-reported only; missing values are unavailable, not zero. " +
                "Profiler ring buffer may retain less than the requested duration. Development/Profiler overhead is included.";
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonConvert.SerializeObject(new
            { schema = 1, firstFrame = first, lastFrame = last, note = caveat, threads = rows }, Formatting.Indented));
            var text = new StringBuilder("# Unity Profiler capture\n\n" + caveat + "\n\n");
            foreach (var row in rows)
            {
                text.AppendLine($"## {row.group} / {row.name}\n\nFrames: {row.frameCount}; mean {row.frameTimeMs.mean:F2} ms; P95 {row.frameTimeMs.p95:F2} ms; max {row.frameTimeMs.max:F2} ms.\n");
                text.AppendLine("| Marker | Self ms/frame | Inclusive ms/frame | Calls/frame |\n| --- | ---: | ---: | ---: |");
                foreach (var marker in row.markers.Take(30))
                    text.AppendLine($"| {marker.name.Replace("|", "\\|").Replace("\n", " ")} | {marker.selfMsPerFrame:F3} | {marker.inclusiveMsPerFrame:F3} | {marker.callsPerFrame:F2} |");
                text.AppendLine();
            }
            string path = Path.Combine(directory, "report.md"); File.WriteAllText(path, text.ToString());
            return path;
        }

        private static Distribution Summarize(IEnumerable<double> input)
        {
            double[] values = input.OrderBy(v => v).ToArray();
            if (values.Length == 0) return new Distribution();
            return new Distribution { count = values.Length, mean = values.Average(),
                p50 = values[(int)Math.Ceiling(values.Length * .5) - 1],
                p95 = values[(int)Math.Ceiling(values.Length * .95) - 1],
                p99 = values[(int)Math.Ceiling(values.Length * .99) - 1], max = values[values.Length - 1] };
        }
        private sealed class Distribution { public int count; public double? mean, p50, p95, p99, max; }
        private sealed class Frame { public int index; public double ms; public double? engineReportedGpuMs; }
        private sealed class Marker { public string name; public double inclusiveMs, selfMs; public long calls; }
        private sealed class ThreadData
        {
            public string name, group;
            public readonly List<Frame> frames = new List<Frame>();
            public readonly Dictionary<string, Marker> markers = new Dictionary<string, Marker>();
        }
    }
}

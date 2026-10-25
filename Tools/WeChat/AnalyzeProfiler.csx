// Execute through Unity MCP. Does not require Play mode or the RedBlue demo.
// Set AppDomain data WeChat.Profiler.Input to an absolute .data path first.
// Loading replaces the current Profiler buffer; recording must already be stopped.
if (UnityEditorInternal.ProfilerDriver.enabled)
    throw new System.InvalidOperationException("Stop Profiler recording before offline analysis.");
string input = System.AppDomain.CurrentDomain.GetData("WeChat.Profiler.Input") as string;
if (string.IsNullOrWhiteSpace(input) || !System.IO.File.Exists(input))
    throw new System.IO.FileNotFoundException("Set WeChat.Profiler.Input to a saved Unity Profiler .data file.");
if (!UnityEditorInternal.ProfilerDriver.LoadProfile(input, false))
    throw new System.IO.IOException("Unable to load " + input);
return ET.WeChatProfilerReport.AnalyzeLoaded(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(input),
    System.IO.Path.GetFileNameWithoutExtension(input) + "-analysis"));

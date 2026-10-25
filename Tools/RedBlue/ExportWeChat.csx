// Unity MCP execute_code method body; shared pipeline preserves/restores build settings.
// Optional AppDomain values: RedBlue.Export.Output (Unity-relative directory),
// RedBlue.Export.Prepare=true (ET + matching HybridCLR generation),
// RedBlue.Export.Profile=true (Development + Autoconnect; forces Prepare),
// RedBlue.Export.RequireDemo=false (profile the normal application instead).
var domain = System.AppDomain.CurrentDomain;
bool profile = object.Equals(domain.GetData("RedBlue.Export.Profile"), true);
bool prepare = profile || object.Equals(domain.GetData("RedBlue.Export.Prepare"), true);
bool requireDemo = !object.Equals(domain.GetData("RedBlue.Export.RequireDemo"), false);
string output = domain.GetData("RedBlue.Export.Output") as string ??
    (profile ? "BuildArtifacts/WeChat-Profiler" : "BuildArtifacts/RedBlue-WeChat");
ET.WeChatProfilerBuild.Queue(output, profile, prepare, requireDemo);
return "Export queued; status: " + ET.WeChatProfilerBuild.StatusPath;

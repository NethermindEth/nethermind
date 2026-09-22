// Resolve call stacks for GCAllocationTick events and report the top allocation sites.
//   dotnet run alloc-stacks.cs -- <file.nettrace> [--type System.String] [--top N]
#:property ManagePackageVersionsCentrally=false
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.16

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

string path = args.Length > 0 ? args[0] : "";
string? typeFilter = null;
int top = 12, frames = 7;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--type") typeFilter = args[i + 1];
    if (args[i] == "--top") int.TryParse(args[i + 1], out top);
    if (args[i] == "--frames") int.TryParse(args[i + 1], out frames);
}
if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Console.Error.WriteLine("need a .nettrace"); return 1; }

string etlx = TraceLog.CreateFromEventPipeDataFile(path);
using TraceLog log = TraceLog.OpenOrConvert(etlx);

Dictionary<string, (long Bytes, int Hits)> byStack = new();
long matched = 0;

foreach (TraceEvent e in log.Events)
{
    if (e is not GCAllocationTickTraceData d) continue;
    if (typeFilter is not null && !string.Equals(d.TypeName, typeFilter, StringComparison.Ordinal)) continue;

    long amount = d.AllocationAmount64 > 0 ? d.AllocationAmount64 : d.AllocationAmount;
    matched += amount;

    List<string> stack = new();
    TraceCallStack? cs = e.CallStack();
    while (cs is not null && stack.Count < frames)
    {
        string name = cs.CodeAddress.FullMethodName;
        if (!string.IsNullOrEmpty(name)) stack.Add(name);
        cs = cs.Caller;
    }
    string key = stack.Count == 0 ? "(no stack)" : string.Join("\n      ", stack);
    byStack.TryGetValue(key, out (long Bytes, int Hits) cur);
    byStack[key] = (cur.Bytes + amount, cur.Hits + 1);
}

Console.WriteLine($"matched {matched / 1048576.0:F1} MB across {byStack.Count:N0} distinct stacks"
                  + (typeFilter is null ? "" : $"   [type = {typeFilter}]"));
Console.WriteLine();
int n = 0;
foreach ((string stack, (long Bytes, int Hits) v) in byStack.OrderByDescending(kv => kv.Value.Bytes).Take(top))
{
    Console.WriteLine($"#{++n}  {100.0 * v.Bytes / matched:F1}%  {v.Bytes / 1048576.0:F1} MB  ({v.Hits} ticks)");
    Console.WriteLine($"      {stack}");
    Console.WriteLine();
}
return 0;

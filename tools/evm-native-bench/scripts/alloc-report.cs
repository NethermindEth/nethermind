// Aggregate GCAllocationTick events in a .nettrace by allocated type.
// The runtime emits one tick per ~100 KB allocated, sampling the allocation that crossed the
// threshold, so the distribution is weighted by bytes rather than by object count.
//
//   dotnet run alloc-report.cs -- <file.nettrace> [--top N]
#:property ManagePackageVersionsCentrally=false
#:package Microsoft.Diagnostics.Tracing.TraceEvent@3.1.16

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

string path = args.Length > 0 ? args[0] : "";
int top = 25;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--top") int.TryParse(args[i + 1], out top);

if (string.IsNullOrEmpty(path) || !File.Exists(path))
{
    Console.Error.WriteLine("usage: dotnet run alloc-report.cs -- <file.nettrace> [--top N]");
    return 1;
}

Dictionary<string, (long Bytes, long Ticks)> byType = new();
long totalBytes = 0, totalTicks = 0;

using (EventPipeEventSource source = new(path))
{
    source.Clr.GCAllocationTick += (GCAllocationTickTraceData d) =>
    {
        string type = string.IsNullOrEmpty(d.TypeName) ? "(unknown)" : d.TypeName;
        long amount = d.AllocationAmount64 > 0 ? d.AllocationAmount64 : d.AllocationAmount;
        byType.TryGetValue(type, out (long Bytes, long Ticks) cur);
        byType[type] = (cur.Bytes + amount, cur.Ticks + 1);
        totalBytes += amount;
        totalTicks++;
    };
    source.Process();
}

if (totalTicks == 0)
{
    Console.Error.WriteLine("no GCAllocationTick events - was the trace collected with the GC keyword at verbose?");
    return 2;
}

Console.WriteLine($"allocation ticks: {totalTicks:N0}   sampled bytes: {totalBytes / 1048576.0:F1} MB");
Console.WriteLine();
Console.WriteLine($"{"share",7}  {"sampled MB",11}  {"ticks",8}  type");
Console.WriteLine(new string('-', 96));
foreach ((string type, (long Bytes, long Ticks) v) in byType.OrderByDescending(kv => kv.Value.Bytes).Take(top))
    Console.WriteLine($"{100.0 * v.Bytes / totalBytes,6:F1}%  {v.Bytes / 1048576.0,11:F2}  {v.Ticks,8:N0}  {type}");

return 0;

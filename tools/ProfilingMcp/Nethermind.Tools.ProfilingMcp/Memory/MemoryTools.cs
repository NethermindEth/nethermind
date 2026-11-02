// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using ModelContextProtocol.Server;

namespace Nethermind.Tools.ProfilingMcp.Memory;

[McpServerToolType]
public sealed class MemoryTools(WorkspacePaths paths)
{
    [McpServerTool(Name = "heap_stats", Title = "Managed heap statistics", Destructive = false, OpenWorld = false)]
    [Description("Walks the managed heap of a process or dump and reports totals, bytes per generation, fragmentation, and the types using the most memory (shallow size). " +
                 "Saves the statistics and returns an id for heap_diff. A live process is paused briefly. Expect minutes on heaps of tens of GB.")]
    public string HeapStats(
        [Description(HeapTarget.Description)] string target,
        [Description("Number of types to list.")] int top = 30,
        [Description("Only list types whose name matches this regex, e.g. ^Nethermind\\.")] string? include = null)
    {
        HeapSnapshot snapshot;
        using (DataTarget dataTarget = HeapTarget.Open(target))
        using (ClrRuntime runtime = HeapTarget.CreateRuntime(dataTarget))
            snapshot = HeapSnapshot.Collect(target, runtime.Heap);

        string id = Path.GetFileNameWithoutExtension(WorkspacePaths.NewFileName(File.Exists(target) ? "dump" : $"pid{target}", ""));
        snapshot.Save(Path.Combine(paths.Heap, id + ".json"));

        StringBuilder sb = new();
        sb.Append($"Heap snapshot id: {id}\n");
        sb.Append($"{Count(snapshot.ObjectCount)} objects, {Size(snapshot.TotalBytes)} live, {Size(snapshot.FreeBytes)} free ({Percent(snapshot.FreeBytes, snapshot.TotalBytes + snapshot.FreeBytes)} fragmentation)\n");
        sb.Append("By generation: ").AppendJoin(", ", snapshot.GenerationBytes.OrderBy(static g => g.Key).Select(static g => $"{g.Key} {Size(g.Value)}")).Append('\n');

        Regex? filter = include is null ? null : new Regex(include, RegexOptions.CultureInvariant);
        TextTable table = new("bytes", "% heap", "count", "LOH bytes", "type");
        foreach (TypeStat type in snapshot.Types.Where(t => filter?.IsMatch(t.Name) ?? true).Take(top))
            table.Add(Size(type.Bytes), Percent(type.Bytes, snapshot.TotalBytes), Count(type.Count), type.LargeObjectBytes > 0 ? Size(type.LargeObjectBytes) : "", type.Name);

        return sb.Append(table).ToString();
    }

    [McpServerTool(Name = "heap_diff", Title = "Compare heap snapshots", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows which types grew or shrank between two heap_stats snapshots, largest byte change first. " +
                 "For leak hunting: snapshot, exercise the suspect workload, snapshot again, and look for types that keep growing.")]
    public string HeapDiff(
        [Description("Heap snapshot id (or JSON path) taken first.")] string baseline,
        [Description("Heap snapshot id (or JSON path) taken later.")] string current,
        [Description("Number of types to list.")] int top = 30)
    {
        HeapSnapshot before = LoadSnapshot(baseline);
        HeapSnapshot after = LoadSnapshot(current);

        StringBuilder sb = new();
        sb.Append($"{before.TakenAtUtc:O} -> {after.TakenAtUtc:O} ({(after.TakenAtUtc - before.TakenAtUtc).TotalSeconds:N0} s)\n");
        sb.Append($"Live: {Size(before.TotalBytes)} -> {Size(after.TotalBytes)} ({SignedSize(after.TotalBytes - before.TotalBytes)}); objects {SignedCount(after.ObjectCount - before.ObjectCount)}\n");

        TextTable table = new("bytes Δ", "count Δ", "bytes now", "count now", "type");
        foreach (TypeDelta delta in HeapSnapshot.Diff(before, after).Take(top))
            table.Add(SignedSize(delta.BytesDelta), SignedCount(delta.CountDelta), Size(delta.Bytes), Count(delta.Count), delta.Name);

        return sb.Append(table).ToString();
    }

    [McpServerTool(Name = "heap_retention", Title = "Why are objects kept alive", Destructive = false, OpenWorld = false)]
    [Description("For instances of the types matching a regex: counts which types reference them and finds the shortest reference chains from GC roots, " +
                 "grouping identical chains. Use it on types that heap_diff shows growing to find the cache, event handler, or static holding them.")]
    public string HeapRetention(
        [Description(HeapTarget.Description)] string target,
        [Description("Regex on the full type name, e.g. ^Nethermind\\.Core\\.Block$ or LeakyCacheEntry$.")] string typePattern,
        [Description("Maximum distinct reference chains to show.")] int maxPaths = 5,
        [Description("Stop the root search after visiting this many objects, to bound time and memory on huge heaps.")] long maxVisited = 20_000_000)
    {
        RetentionReport report;
        using (DataTarget dataTarget = HeapTarget.Open(target))
        using (ClrRuntime runtime = HeapTarget.CreateRuntime(dataTarget))
            report = RetentionAnalyzer.Analyze(runtime, new Regex(typePattern, RegexOptions.CultureInvariant), maxPaths, maxVisited);

        if (report.InstanceCount == 0)
            return $"No live instances of types matching '{typePattern}'.";

        StringBuilder sb = new();
        sb.Append($"{Count(report.InstanceCount)} instances, {Size(report.InstanceBytes)} (shallow) of: ").AppendJoin(", ", report.MatchedTypes.Take(10)).Append('\n');
        sb.Append("\nReferenced by (includes unreachable referrers the GC has not collected yet):\n");
        TextTable referrers = new("references", "from type");
        foreach (KeyValuePair<string, long> referrer in report.Referrers.Take(15))
            referrers.Add(Count(referrer.Value), referrer.Key);
        sb.Append(referrers);

        sb.Append(report.SearchComplete ? "\nShortest paths from GC roots:\n" : "\nShortest paths from GC roots (search stopped early; counts are partial):\n");
        foreach (RetentionPath path in report.Paths)
        {
            sb.Append($"[{path.RootKind}] retains {Count(path.Instances)} instance(s) via:\n");
            foreach (PathStep step in path.Steps)
                sb.Append("  ").Append(step.Field is null ? "" : $".{step.Field} -> ").Append(step.TypeName).Append($" @0x{step.Address:x}\n");
        }

        if (report.Paths.Count == 0)
            sb.Append("  None reached; the instances are unreachable and will be freed by the next full GC.\n");

        return sb.Append($"\nTook: {report.Timings}\n").ToString();
    }

    [McpServerTool(Name = "alloc_sample", Title = "Sample allocations by type", ReadOnly = true, OpenWorld = false)]
    [Description("Samples which types a running process allocates over a time window, using the runtime's allocation tick events (about one per 100 KB). " +
                 "Use it to find allocation hotspots that drive GC pressure, then trace_hotspots or trace_callstacks to find the allocating code.")]
    public async Task<string> AllocSampleAsync(
        [Description("Process ID.")] int pid,
        [Description("Seconds to sample.")] int seconds = 10,
        [Description("Number of types to list.")] int top = 30,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AllocationStat> stats = await EventPipeCollector.SampleAllocationsAsync(pid, TimeSpan.FromSeconds(seconds), cancellationToken);
        long total = 0;
        foreach (AllocationStat stat in stats)
            total += stat.SampledBytes;

        TextTable table = new("~bytes", "% allocs", "~bytes/s", "events", "type");
        foreach (AllocationStat stat in stats.Take(top))
            table.Add(Size(stat.SampledBytes), Percent(stat.SampledBytes, total), Size(stat.SampledBytes / Math.Max(seconds, 1)), Count(stat.Events), stat.TypeName);

        return $"~{Size(total)} allocated in {seconds} s (~{Size(total / Math.Max(seconds, 1))}/s), approximated from allocation ticks.\n{table}";
    }

    [McpServerTool(Name = "runtime_counters", Title = "Read .NET runtime counters", ReadOnly = true, OpenWorld = false)]
    [Description("Observes a running process over a time window: GC counts by generation and reason, GC pause time, heap size per generation, " +
                 "plus the System.Runtime counters when the app has EventSource enabled (allocation rate, thread pool queue, lock contention, exceptions, CPU). " +
                 "A quick first check of whether a problem is CPU, GC, or contention.")]
    public async Task<string> RuntimeCountersAsync(
        [Description("Process ID.")] int pid,
        [Description("Seconds to observe.")] int seconds = 10,
        CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<CounterSummary> counters, GcSummary gc) = await EventPipeCollector.CollectCountersAsync(pid, TimeSpan.FromSeconds(seconds), intervalSeconds: 1, cancellationToken);

        StringBuilder sb = new();
        int gcCount = gc.CountsByGeneration.Values.Sum();
        sb.Append($"GCs in {seconds} s: {gcCount} (").AppendJoin(", ", gc.CountsByGeneration.OrderBy(static g => g.Key).Select(static g => $"gen{g.Key} {g.Value}")).Append(")\n");
        if (gcCount > 0)
        {
            sb.Append("Reasons: ").AppendJoin(", ", gc.CountsByReason.OrderByDescending(static r => r.Value).Select(static r => $"{r.Key} {r.Value}")).Append('\n');
            sb.Append($"Pauses: total {Number(gc.TotalPauseMs)} ms ({Number(gc.TotalPauseMs / (seconds * 10.0))} % of wall time), max {Number(gc.MaxPauseMs)} ms\n");
        }
        if (gc.LastHeapBytes is { } heap)
            sb.Append("Heap after last GC: ").AppendJoin(", ", heap.Select(static g => $"{g.Key} {Size(g.Value)}")).Append($"; promoted {Size(gc.LastPromotedBytes)}\n");

        if (counters.Count == 0)
            return sb.Append("\nNo System.Runtime counters: the app disables EventSource (System.Diagnostics.Tracing.EventSource.IsSupported=false) or the window was under 1 s. Use alloc_sample for allocation rates.").ToString();

        TextTable table = new("min", "avg", "max", "last", "units", "counter");
        foreach (CounterSummary c in counters)
            table.Add(Number(c.Min), Number(c.Average), Number(c.Max), Number(c.Last), c.IsRate ? $"{c.Units}/interval" : c.Units, c.DisplayName);
        return sb.Append('\n').Append(table).ToString();
    }

    [McpServerTool(Name = "write_dump", Title = "Write a process dump", Destructive = false, OpenWorld = false)]
    [Description("Writes a dump with the full managed heap of a running process, for repeated offline analysis with heap_stats and heap_retention " +
                 "without pausing the process again. The file is roughly as large as the process's memory.")]
    public async Task<string> WriteDumpAsync([Description("Process ID.")] int pid, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(paths.Heap, WorkspacePaths.NewFileName($"pid{pid}", ".dmp"));
        await new DiagnosticsClient(pid).WriteDumpAsync(DumpType.WithHeap, path, logDumpGeneration: false, cancellationToken);
        return $"Dump written: {path} ({Size(new FileInfo(path).Length)})";
    }

    private HeapSnapshot LoadSnapshot(string idOrPath) =>
        HeapSnapshot.Load(File.Exists(idOrPath) ? idOrPath : Path.Combine(paths.Heap, idOrPath + ".json"));

    private static string Size(long bytes)
    {
        double abs = Math.Abs((double)bytes);
        return abs switch
        {
            >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.00 GB", CultureInfo.InvariantCulture),
            >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0 MB", CultureInfo.InvariantCulture),
            >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.0 KB", CultureInfo.InvariantCulture),
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        };
    }

    private static string SignedSize(long bytes) => bytes > 0 ? "+" + Size(bytes) : Size(bytes);

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string SignedCount(long value) => value.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    private static string Percent(long part, long total) => total > 0 ? ((double)part / total).ToString("P1", CultureInfo.InvariantCulture) : "-";
}

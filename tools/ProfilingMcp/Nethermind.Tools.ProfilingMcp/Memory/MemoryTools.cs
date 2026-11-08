// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using ModelContextProtocol.Server;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using Nethermind.Tools.ProfilingMcp.Native;

namespace Nethermind.Tools.ProfilingMcp.Memory;

[McpServerToolType]
public sealed class MemoryTools(WorkspacePaths paths)
{
    [McpServerTool(Name = "heap_stats", Title = "Managed heap statistics", Destructive = false, OpenWorld = false)]
    [Description("Reports the managed heap of a process or dump: totals, bytes per generation, fragmentation, and the types using the most memory (shallow size). " +
                 "Saves the statistics and returns an id for heap_diff. " + LiveHeapNote)]
    public async Task<string> HeapStatsAsync(
        [Description(HeapTarget.Description)] string target,
        [Description("Number of types to list.")] int top = 30,
        [Description("Only list types whose name matches this regex, e.g. ^Nethermind\\.")] string? include = null,
        CancellationToken cancellationToken = default)
    {
        HeapSnapshot snapshot;
        StringBuilder sb = new();
        if (HeapTarget.IsProcess(target, out int pid))
        {
            GcHeapDumpResult dump = await GcHeapDump.CollectAsync(pid, withReferences: false, cancellationToken);
            snapshot = dump.Snapshot;
            sb.Append(Describe(dump));
        }
        else
        {
            using DataTarget dataTarget = HeapTarget.OpenDump(target);
            using ClrRuntime runtime = HeapTarget.CreateRuntime(dataTarget);
            snapshot = HeapSnapshot.Collect(target, runtime.Heap);
            sb.Append("Read from the dump, including unreachable objects the GC has not collected yet.\n");
        }

        string id = Path.GetFileNameWithoutExtension(WorkspacePaths.NewFileName(File.Exists(target) ? "dump" : $"pid{target}", ""));
        snapshot.Save(Path.Combine(paths.Heap, id + ".json"));

        sb.Insert(0, $"Heap snapshot id: {id}\n");
        sb.Append($"{Count(snapshot.ObjectCount)} objects, {Size(snapshot.TotalBytes)} live, {Size(snapshot.FreeBytes)} free ({Percent(snapshot.FreeBytes, snapshot.TotalBytes + snapshot.FreeBytes)} fragmentation)\n");
        sb.Append("By generation: ").AppendJoin(", ", snapshot.GenerationBytes.OrderBy(static g => g.Key).Select(static g => $"{g.Key} {Size(g.Value)}")).Append('\n');

        Regex? filter = ToolArguments.OptionalPattern(include);
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
                 "grouping identical chains. Use it on types that heap_diff shows growing to find the cache, event handler, or static holding them. " +
                 "Chains from a process name types only; chains from a dump also name fields and static fields. " + LiveHeapNote)]
    public async Task<string> HeapRetentionAsync(
        [Description(HeapTarget.Description)] string target,
        [Description("Regex on the full type name, e.g. ^Nethermind\\.Core\\.Block$ or LeakyCacheEntry$.")] string typePattern,
        [Description("Maximum distinct reference chains to show.")] int maxPaths = 5,
        [Description("Stop the root search after visiting this many objects, to bound time and memory on huge heaps.")] long maxVisited = 20_000_000,
        CancellationToken cancellationToken = default)
    {
        Regex pattern = ToolArguments.Pattern(typePattern);
        RetentionReport report;
        StringBuilder sb = new();
        if (HeapTarget.IsProcess(target, out int pid))
        {
            GcHeapDumpResult dump = await GcHeapDump.CollectAsync(pid, withReferences: true, cancellationToken);
            sb.Append(Describe(dump));
            report = RetentionAnalyzer.Analyze(dump.Graph!, pattern, maxPaths, maxVisited);
        }
        else
        {
            using DataTarget dataTarget = HeapTarget.OpenDump(target);
            using ClrRuntime runtime = HeapTarget.CreateRuntime(dataTarget);
            report = RetentionAnalyzer.Analyze(runtime, pattern, maxPaths, maxVisited);
        }

        if (report.InstanceCount == 0)
            return sb.Append($"No live instances of types matching '{typePattern}'.").ToString();

        sb.Append($"{Count(report.InstanceCount)} instances, {Size(report.InstanceBytes)} (shallow) of: ").AppendJoin(", ", report.MatchedTypes.Take(10)).Append('\n');
        sb.Append("\nReferenced by (includes unreachable referrers the GC has not collected yet):\n");
        TextTable referrers = new("references", "from type");
        foreach (KeyValuePair<string, long> referrer in report.Referrers.Take(15))
            referrers.Add(Count(referrer.Value), referrer.Key);
        sb.Append(referrers);

        sb.Append(report.SearchComplete ? "\nShortest paths from GC roots:\n" : "\nShortest paths from GC roots (search stopped early; counts are partial):\n");
        foreach (RetentionPath path in report.Paths)
        {
            sb.Append($"[{path.RootKind}] retains {Count(path.Instances)} instance(s), {Size(path.Bytes)}, via:\n");
            foreach (PathStep step in path.Steps)
                sb.Append("  ").Append(step.Field is null ? "" : $".{step.Field} -> ").Append(step.TypeName).Append($" @0x{step.Address:x}\n");
        }

        if (report.Paths.Count == 0)
            sb.Append(NoPathsNote(report.SearchComplete, maxVisited));

        return sb.Append($"\nTook: {report.Timings}\n").ToString();
    }

    /// <summary>What finding no path from a GC root means: unreachable only when the search saw every reachable object.</summary>
    internal static string NoPathsNote(bool searchComplete, long maxVisited) => searchComplete
        ? "  None reached; the instances are unreachable and will be freed by the next full GC.\n"
        : $"  None found within the {Count(maxVisited)} objects visited; the instances may still be reachable. Raise maxVisited to search further.\n";

    [McpServerTool(Name = "alloc_sample", Title = "Sample allocations by type and code", Destructive = false, OpenWorld = false)]
    [Description("Samples what a running process allocates over a time window, using the runtime's allocation tick events (about one per 100 KB) with their call stacks: " +
                 "the types allocated, the methods allocating them (innermost frame outside System.* and Microsoft.*), and the heaviest call stacks. " +
                 "Use it to find allocation hotspots that drive GC pressure; include narrows it to the allocations of one component.")]
    public async Task<string> AllocSampleAsync(
        [Description("Process ID.")] int pid,
        [Description(ToolArguments.SecondsDescription)] int seconds = 10,
        [Description("Number of rows per table.")] int top = 20,
        [Description("Only count allocations with a call-stack frame matching this regex, e.g. Kademlia or ^Nethermind\\.Network\\.")] string? include = null,
        [Description("Number of heaviest call stacks to show.")] int stacks = 5,
        CancellationToken cancellationToken = default)
    {
        string trace = Path.Combine(paths.Snapshots, WorkspacePaths.NewFileName($"pid{pid}-alloc", ".nettrace"));
        await AllocationSampler.CollectAsync(pid, ToolArguments.Window(seconds), trace, cancellationToken);
        AllocationProfile profile = AllocationSampler.Read(trace, ToolArguments.OptionalPattern(include));

        long total = profile.ByType.Sum(static s => s.SampledBytes);
        long perSecond = total / Math.Max(seconds, 1);
        StringBuilder sb = new();
        sb.Append($"Trace saved: {paths.ForPerson(trace)}\n");
        sb.Append($"~{Size(total)} allocated in {seconds} s (~{Size(perSecond)}/s){(include is null ? "" : $" with a frame matching '{include}'")}, approximated from allocation ticks.\n");
        if (total == 0)
            return sb.ToString();

        sb.Append("\nBy type:\n").Append(Table(profile.ByType, "type", "mostly allocated in"));
        sb.Append("\nBy allocating method:\n").Append(Table(profile.BySite, "method", "mostly of type"));
        foreach ((IReadOnlyList<string> frames, long events, long bytes) in profile.Stacks.Take(stacks))
            sb.Append($"\n~{Size(bytes)} ({Percent(bytes, total)}, {Count(events)} events):\n  {string.Join("\n  ", frames)}\n");
        return sb.ToString();

        TextTable Table(IReadOnlyList<AllocationStat> stats, string name, string topHeader)
        {
            TextTable table = new("~bytes", "% allocs", "~bytes/s", "events", name, topHeader);
            foreach (AllocationStat stat in stats.Take(top))
                table.Add(Size(stat.SampledBytes), Percent(stat.SampledBytes, total), Size(stat.SampledBytes / Math.Max(seconds, 1)), Count(stat.Events), stat.Name, stat.Top);
            return table;
        }
    }

    [McpServerTool(Name = "runtime_counters", Title = "Read .NET runtime counters", ReadOnly = true, OpenWorld = false)]
    [Description("Observes a running process over a time window: GC counts by generation and reason, GC pause time, heap size per generation, " +
                 "plus the System.Runtime counters when the app has EventSource enabled (allocation rate, thread pool queue, lock contention, exceptions, CPU). " +
                 "A quick first check of whether a problem is CPU, GC, or contention.")]
    public async Task<string> RuntimeCountersAsync(
        [Description("Process ID.")] int pid,
        [Description("Seconds to observe, 1 to 600.")] int seconds = 10,
        CancellationToken cancellationToken = default)
    {
        (IReadOnlyList<CounterSummary> counters, GcSummary gc) = await EventPipeCollector.CollectCountersAsync(pid, ToolArguments.Window(seconds), intervalSeconds: 1, cancellationToken);

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
                 "without pausing the process again. The process is frozen while the dump is written, for tens of seconds: a Nethermind node at 2 GB RSS " +
                 "was paused for about 40 s and wrote a 5 GB file. That is long enough for a node to miss Engine API deadlines and its validator to miss duties, " +
                 "so ask before dumping a production node, and prefer heap_stats and heap_retention on the PID. The file is roughly as large as the process's memory.")]
    public async Task<string> WriteDumpAsync([Description("Process ID.")] int pid, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(paths.Heap, WorkspacePaths.NewFileName($"pid{pid}", ".dmp"));
        if (paths.Sidecar is null)
        {
            await new DiagnosticsClient(pid).WriteDumpAsync(DumpType.WithHeap, path, logDumpGeneration: false, cancellationToken);
            return $"Dump written: {path} ({Size(new FileInfo(path).Length)})";
        }

        // The target's runtime writes the dump, as the target's user and without access to the sidecar's files, so it goes to the
        // target's temp folder, where the runtime keeps its diagnostic socket, and is moved from there.
        string inTarget = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
        try
        {
            await new DiagnosticsClient(pid).WriteDumpAsync(DumpType.WithHeap, inTarget[ContainerSidecar.TargetRoot.Length..], logDumpGeneration: false, cancellationToken);
            File.Move(inTarget, path);
        }
        finally
        {
            File.Delete(inTarget);
        }

        return $"Dump written: {path} ({Size(new FileInfo(path).Length)})";
    }

    [McpServerTool(Name = "native_memory", Title = "Where a process's memory is", Destructive = false, OpenWorld = false)]
    [Description("Splits the resident memory (RSS) of a Linux process into what holds it: the GC heap by generation, the .NET runtime's own heaps and JIT code, " +
                 "thread stacks, glibc malloc (arenas and large blocks), other anonymous mmaps such as other allocators' (RocksDB may bring jemalloc), and mapped files. " +
                 "Answers whether memory growth is managed or native; use heap_stats for what is in the GC heap. " +
                 "Reads /proc and, for a .NET process, the runtime with ClrMD, without suspending or pausing the process. " +
                 "Also writes an HTML page for people: a zoomable treemap of the memory down to single mappings, files and threads, and a category table. " +
                 "It cannot name the code that allocated native memory. Linux only, so on Windows pass a container.")]
    public string NativeMemoryBreakdown(
        [Description("Process ID; in a container, a PID from list_dotnet_processes or docker top.")] int pid,
        [Description("Number of mapped files to list.")] int topFiles = 10)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("native_memory reads /proc, which exists on Linux only; profile a container or a Linux host.");

        IReadOnlyList<Mapping> mappings = Native.NativeMemory.ParseSmaps(File.ReadLines($"/proc/{pid}/smaps"));
        (IReadOnlyList<KnownRange> Ranges, long GcCommitted)? runtime = Native.NativeMemory.RuntimeRanges(pid);
        List<KnownRange> known = [.. runtime?.Ranges ?? [], .. Native.NativeMemory.ThreadStackRanges(pid, mappings), .. Native.NativeMemory.MallocMmappedRanges(pid, mappings)];
        IReadOnlyList<MemoryShare> shares = Native.NativeMemory.Attribute(mappings, known);
        IReadOnlyList<MemoryCategory> categories = Native.NativeMemory.Categorize(shares);

        string report = Path.Combine(paths.Heap, WorkspacePaths.NewFileName($"pid{pid}-memory", ".html"));
        File.WriteAllText(report, MemoryReport.Render($"PID {pid}", shares, runtime?.GcCommitted));

        long rss = mappings.Sum(static m => m.Rss);
        long swap = mappings.Sum(static m => m.Swap);
        StringBuilder sb = new($"Report written: {paths.ForPerson(report)}\nRSS {Size(rss)}" + (swap > 0 ? $", swapped out {Size(swap)}" : "") + $", {Count(mappings.Count)} mappings.\n");
        sb.Append(runtime is { } clr
            ? $"GC heap committed: {Size(clr.GcCommitted)}. GC regions, runtime heaps and managed thread stacks are located through the runtime.\n"
            : "Not read as .NET (not a .NET process, or ClrMD could not read it): the GC heap shows as anonymous memory.\n");

        long managed = categories.Where(static c => c.Name.StartsWith(Native.NativeMemory.GcPrefix, StringComparison.Ordinal)).Sum(static c => c.Rss);
        long nativeHeap = categories.Where(static c => c.Name is Native.NativeMemory.MallocArenas or Native.NativeMemory.MallocMain or Native.NativeMemory.MallocMmapped or Native.NativeMemory.OtherAnonymous).Sum(static c => c.Rss);
        sb.Append($"Managed (GC heap) {Size(managed)} ({Percent(managed, rss)}); native heap and mmaps {Size(nativeHeap)} ({Percent(nativeHeap, rss)}).\n");

        TextTable table = new("rss", "% rss", "swap", "mappings", "category");
        foreach (MemoryCategory category in categories)
            table.Add(Size(category.Rss), Percent(category.Rss, rss), category.Swap > 0 ? Size(category.Swap) : "", Count(category.Mappings), category.Name);
        sb.Append(table);

        TextTable files = new("rss", "file");
        foreach (IGrouping<string, Mapping> file in mappings.Where(static m => m.Name.StartsWith('/') && !m.Name.StartsWith("/memfd:", StringComparison.Ordinal)).GroupBy(static m => m.Name)
                     .OrderByDescending(static f => f.Sum(static m => m.Rss)).Take(topFiles))
            files.Add(Size(file.Sum(static m => m.Rss)), file.Key);
        sb.Append("\nLargest mapped files:\n").Append(files);

        return sb.Append("\nAnonymous mappings are split by address; one that is only partly a known range is split in proportion to its bytes, so small categories are estimates. " +
                         "malloc keeps freed memory in its arenas, so arena RSS is the native heap's high-water mark rather than what is in use.\n").ToString();
    }

    private const string LiveHeapNote = "For a process ID, the runtime runs one full blocking GC and reports the objects that survive it, so only live objects count. " +
                                        "The process is paused for that GC: about 0.5 s per GB of live heap (390-550 ms on a Nethermind node with 811 MB live), and the pause is reported. " +
                                        "It needs no ptrace and works in containers.";

    private static string Describe(GcHeapDumpResult dump)
    {
        string text = $"Read over EventPipe in {dump.Elapsed.TotalSeconds:N1} s; the induced GC paused the process for {Number(dump.PauseMs)} ms. Only live objects are counted.\n";
        return dump.EventsLost > 0
            ? text + $"WARNING: the runtime dropped {Count(dump.EventsLost)} events, so the heap is incomplete.\n"
            : text;
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

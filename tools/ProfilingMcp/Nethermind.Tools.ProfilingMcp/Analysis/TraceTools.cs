// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Nethermind.Tools.ProfilingMcp.EventPipe;
using Nethermind.Tools.ProfilingMcp.Memory;
using Nethermind.Tools.ProfilingMcp.Native;

namespace Nethermind.Tools.ProfilingMcp.Analysis;

[McpServerToolType]
public sealed class TraceTools(NetTraceReader netTrace, WorkspacePaths paths)
{
    private const string SnapshotDescription = "EventPipe trace (*.nettrace) from cpu_sample, or native stacks (*.folded) from native_sample.";

    [McpServerTool(Name = "cpu_sample", Title = "Sample a process over EventPipe", Destructive = false, OpenWorld = false)]
    [Description("Samples the managed threads of a running process for a time window over EventPipe, with GC and exception events, and saves an EventPipe trace " +
                 "for trace_hotspots, trace_callstacks, trace_report and trace_compare. Returns a summary: hotspots, GC pauses and thrown exceptions. " +
                 "It can be repeated any number of times, never needs the process restarted, and is analyzed on any OS. " +
                 "Native threads and native code are not sampled; time a thread spends in native code shows as own time of the managed method that called it.")]
    public async Task<string> CpuSampleAsync(
        [Description("Process ID.")] int pid,
        [Description("Seconds to sample.")] int seconds = 30,
        [Description("Number of hotspot rows in the summary.")] int top = 15,
        CancellationToken cancellationToken = default)
    {
        string trace = Path.Combine(paths.Snapshots, WorkspacePaths.NewFileName($"pid{pid}", ".nettrace"));
        await CpuSampler.CollectAsync(pid, TimeSpan.FromSeconds(seconds), trace, cancellationToken);

        NetTraceProfile profile = netTrace.Read(trace);
        StringBuilder sb = new();
        sb.Append($"Trace saved: {paths.ForPerson(trace)} ({new FileInfo(trace).Length / (1 << 20):N0} MB)\n");
        sb.Append($"{profile.Threads} managed threads over {profile.DurationMs / 1000:N1} s.\n");

        GcSummary gc = profile.Gc;
        int gcCount = gc.CountsByGeneration.Values.Sum();
        sb.Append($"GCs: {gcCount} (").AppendJoin(", ", gc.CountsByGeneration.OrderBy(static g => g.Key).Select(static g => $"gen{g.Key} {g.Value}"))
            .Append($"), pauses total {Ms(gc.TotalPauseMs)} ms, max {Ms(gc.MaxPauseMs)} ms");
        if (gcCount > 0)
            sb.Append("; reasons: ").AppendJoin(", ", gc.CountsByReason.OrderByDescending(static r => r.Value).Select(static r => $"{r.Key} {r.Value}"));
        sb.Append('\n');

        int thrown = profile.Exceptions.Sum(static e => e.Count);
        sb.Append($"Exceptions thrown: {thrown}\n");
        if (thrown > 0)
        {
            TextTable exceptions = new("count", "type: message", "thrown in");
            foreach (ExceptionStat e in profile.Exceptions.Take(8))
                exceptions.Add(Count(e.Count), $"{e.Type}: {Truncate(e.Message, 80)}", e.ThrowSite);
            sb.Append(exceptions);
        }

        return sb.Append('\n').Append(Hotspots(trace, top: top)).ToString();
    }

    [McpServerTool(Name = "native_sample", Title = "Sample a process's native and managed code with perf", Destructive = false, OpenWorld = false)]
    [Description("Samples the CPU use of every thread of a Linux process with perf, native code included: native libraries such as RocksDB, the .NET runtime and GC, " +
                 "the kernel, and JIT-compiled .NET methods, which are named without restarting a .NET process. Also works for processes that are not .NET. " +
                 "Saves the stacks (*.folded) for trace_hotspots, trace_callstacks, trace_report and trace_compare, and returns CPU time per thread and the hotspots. " +
                 "Measures CPU time only: blocked threads are not sampled. Use cpu_sample for wall time, GC pauses and exceptions of a .NET process. " +
                 "Linux only, so on Windows pass a container.")]
    public async Task<string> NativeSampleAsync(
        [Description("Process ID; in a container, a PID from list_dotnet_processes or docker top.")] int pid,
        [Description("Seconds to sample.")] int seconds = 30,
        [Description("Number of hotspot rows in the summary.")] int top = 25,
        CancellationToken cancellationToken = default)
    {
        string stacks = Path.Combine(paths.Snapshots, WorkspacePaths.NewFileName($"pid{pid}-native", NativeSampler.Extension));
        (string PerfMap, int Methods)? jit = await NativeSampler.CollectAsync(pid, TimeSpan.FromSeconds(seconds), stacks, cancellationToken);
        IReadOnlyList<FunctionStat> functions = FoldedStacks.Read(File.ReadLines(stacks));

        StringBuilder sb = new();
        sb.Append($"Stacks saved: {paths.ForPerson(stacks)}\n");
        sb.Append(jit switch
        {
            null => "JIT-compiled code is unnamed ([memfd:doublemapper] or [unknown]): the process is not .NET, or its diagnostics port is unavailable.\n",
            ({ } map, 0) => $"JIT-compiled code is unnamed: the runtime wrote no perf map at {map}.\n",
            (_, int methods) => $"JIT-compiled code is named from the runtime's perf map ({methods:N0} methods).\n",
        });

        FunctionStat[] threads = [.. functions.Where(static f => f.Name.StartsWith("[thread ", StringComparison.Ordinal)).OrderByDescending(static f => f.TotalTime)];
        double cpu = threads.Sum(static f => f.TotalTime);
        sb.Append($"CPU time: {Ms(cpu)} ms over {seconds} s ({cpu / 10 / seconds:N0} % of one core). By thread name:\n");
        TextTable byThread = new("cpu ms", "cpu %", "threads");
        foreach (FunctionStat thread in threads.Take(10))
            byThread.Add(Ms(thread.TotalTime), Percent(thread.TotalTime, cpu), thread.Name["[thread ".Length..^1]);
        sb.Append(byThread).Append('\n');

        return sb.Append(Hotspots(stacks, top: top, exclude: @"^\[thread ", hideWaits: false)).ToString();
    }

    [McpServerTool(Name = "trace_hotspots", Title = "Rank methods in a CPU trace", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Ranks methods in an EventPipe trace or native stacks by own time (time in the method body) or total time (including callees). " +
                 "Times are milliseconds summed over all threads: wall time for EventPipe traces, CPU time for native stacks.")]
    public string Hotspots(
        [Description(SnapshotDescription)] string snapshot,
        [Description("own or total.")] string sortBy = "own",
        [Description("Number of rows.")] int top = 30,
        [Description("Only methods whose full name matches this regex, e.g. ^Nethermind\\.")] string? include = null,
        [Description("Hide methods whose full name matches this regex, e.g. ^System\\.")] string? exclude = null,
        [Description("Hide methods where threads block (Monitor.Wait, semaphore and socket waits, Thread.Sleep) and compute percentages over the remaining time. " +
                     "EventPipe sampling measures wall time, so on a mostly idle process these dominate.")] bool hideWaits = true)
    {
        IReadOnlyList<FunctionStat> functions = ReadSampled(snapshot);
        double totalOwn = 0;
        double waitOwn = 0;
        foreach (FunctionStat f in functions)
        {
            totalOwn += f.OwnTime;
            if (BlockingWait.IsMatch(f.Name))
                waitOwn += f.OwnTime;
        }

        bool byTotal = sortBy.Equals("total", StringComparison.OrdinalIgnoreCase);
        IEnumerable<FunctionStat> rows = Filter(functions, include, exclude)
            .Where(f => !hideWaits || !BlockingWait.IsMatch(f.Name))
            .OrderByDescending(f => byTotal ? f.TotalTime : f.OwnTime)
            .Take(top);

        double basis = hideWaits ? totalOwn - waitOwn : totalOwn;
        TextTable table = new("own ms", "own %", "total ms", "samples", "method");
        foreach (FunctionStat f in rows)
            table.Add(Ms(f.OwnTime), Percent(f.OwnTime, basis), Ms(f.TotalTime), Count(f.Samples), f.Name);

        string waits = hideWaits
            ? $" Hidden: {Ms(waitOwn)} ms ({Percent(waitOwn, totalOwn)}) blocked in waits; own % is relative to the remaining {Ms(basis)} ms."
            : "";
        string totals = byTotal && hideWaits ? " Total times still include waits in callees." : "";
        return $"{functions.Count} methods; own time over all threads: {Ms(totalOwn)} ms.{waits} Sorted by {(byTotal ? "total" : "own")} time.{totals}\n{table}";
    }

    private static readonly Regex BlockingWait = new(
        @"^(System\.Threading\.(Monitor\.Wait|LowLevelLifoSemaphore\.WaitForSignal|LowLevelMonitor\.Wait|SemaphoreSlim\.(WaitUntilCountOrTimeout|WaitCore)|WaitHandle\.WaitOne\w*|WaitHandle\.WaitMultiple\w*|Thread\.Sleep|ManualResetEventSlim\.Wait)" +
        @"|System\.GC\.RunFinalizers|System\.Net\.Sockets\.SocketAsyncEngine\.EventLoop" +
        @"|Interop\+Sys\.(Read|WaitForSocketEvents|Poll)|Interop\+Kernel32\.(GetQueuedCompletionStatus\w*|WaitFor\w+))$",
        RegexOptions.CultureInvariant);

    [McpServerTool(Name = "trace_callstacks", Title = "Show call stacks reaching a method", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the distinct call stacks (outermost caller first) that reach methods matching a regex, ranked by time, to find which callers make a hotspot expensive.")]
    public string CallStacks(
        [Description(SnapshotDescription)] string snapshot,
        [Description("Regex on the full method name; escape dots and plus signs, e.g. Nethermind\\.Trie\\.PatriciaTree\\.Get.")] string methodPattern,
        [Description("Maximum stacks to show per method.")] int maxStacks = 10,
        [Description("Maximum frames to show per stack, keeping the innermost.")] int maxFrames = 25)
    {
        IReadOnlyList<FunctionStat> functions = GetCallStacks(snapshot, methodPattern);
        if (functions.Count == 0)
            return $"No method matches '{methodPattern}'. Use trace_hotspots to find exact names.";

        StringBuilder sb = new();
        foreach (FunctionStat f in functions.OrderByDescending(f => f.TotalTime).Take(10))
        {
            sb.Append($"{f.Name}: total {Ms(f.TotalTime)} ms, own {Ms(f.OwnTime)} ms, {f.CallStacks.Count} distinct stacks\n");
            foreach (CallStackStat stack in f.CallStacks.OrderByDescending(s => s.TotalTime).Take(maxStacks))
            {
                sb.Append($"  total {Ms(stack.TotalTime)} ms, own {Ms(stack.OwnTime)} ms, samples {Count(stack.Samples)}\n");
                int skipped = Math.Max(0, stack.Frames.Count - maxFrames);
                if (skipped > 0)
                    sb.Append($"    ... {skipped} outer frames\n");
                for (int i = skipped; i < stack.Frames.Count; i++)
                    sb.Append("    ").Append(stack.Frames[i]).Append('\n');
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "trace_report", Title = "Write an HTML report of a CPU trace", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes a self-contained HTML page for a person to explore a trace or snapshot in a browser: an interactive flame graph of the call tree and a sortable hotspot table, " +
                 "both with search and a blocking-wait filter. Use it when someone asks to see the results; read them yourself with trace_hotspots and trace_callstacks.")]
    public async Task<string> ReportAsync(
        [Description(SnapshotDescription)] string snapshot,
        [Description("Where to write the page. Defaults to the snapshot path with .html appended.")] string? output = null,
        [Description("Maximum call-tree nodes in the flame graph; the smallest call paths are dropped to fit.")] int maxNodes = 5000,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FunctionStat> functions = GetCallStacks(snapshot, ".*");
        CallTreeNode tree = CallTree.Build(functions, BlockingWait.IsMatch);
        int nodesBeforePruning = CallTree.Count(tree);
        int kept = CallTree.Prune(tree, maxNodes);

        output ??= snapshot + ".html";
        await File.WriteAllTextAsync(output, HtmlReport.Render(snapshot, functions, tree, nodesBeforePruning, BlockingWait.IsMatch), cancellationToken);
        return $"Report written: {paths.ForPerson(output)}\nFlame graph shows {kept:N0} of {nodesBeforePruning:N0} call paths; {Percent(tree.WaitTime, tree.TotalTime)} of sampled time is in blocking waits (hidden by default on the page).";
    }

    [McpServerTool(Name = "trace_compare", Title = "Compare two CPU traces", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows per-method time changes from a baseline trace to a current one of the same kind (current minus baseline), largest first. Use it to verify a fix. " +
                 "Make both cover the same workload, or the deltas will mostly reflect the difference in run length.")]
    public string Compare(
        [Description("Trace before the change (*.nettrace or *.folded).")] string baseline,
        [Description("Trace after the change, in the same format as the baseline.")] string current,
        [Description("Number of rows.")] int top = 30,
        [Description("Only methods whose full name matches this regex.")] string? include = null)
    {
        if (NativeSampler.IsFolded(baseline) != NativeSampler.IsFolded(current))
            throw new ArgumentException("Compare two traces of the same kind: EventPipe traces measure wall time, native stacks CPU time.");

        IReadOnlyList<FunctionStat> deltas = Diff(ReadSampled(baseline), ReadSampled(current));
        TextTable table = new("own Δ ms", "total Δ ms", "method");
        foreach (FunctionStat f in Filter(deltas, include, exclude: null).Where(static f => f.OwnTime != 0 || f.TotalTime != 0).OrderByDescending(static f => Math.Abs(f.OwnTime)).Take(top))
            table.Add(SignedMs(f.OwnTime), SignedMs(f.TotalTime), f.Name);

        return table.Count == 0 ? "No differences." : $"Current minus baseline, sorted by |own Δ|. Negative is faster.\n{table}";
    }

    private IReadOnlyList<FunctionStat> GetCallStacks(string snapshot, string methodPattern)
    {
        Regex pattern = new(methodPattern, RegexOptions.CultureInvariant);
        return [.. ReadSampled(snapshot).Where(f => pattern.IsMatch(f.Name))];
    }

    private IReadOnlyList<FunctionStat> ReadSampled(string path) =>
        NetTraceReader.IsNetTrace(path) ? netTrace.Read(path).Functions
        : NativeSampler.IsFolded(path) ? FoldedStacks.Read(File.ReadLines(path))
        : throw new ArgumentException($"'{path}' is neither an EventPipe trace (*.nettrace) nor native stacks (*.folded).");

    /// <summary>Per-method deltas from <paramref name="baseline"/> to <paramref name="current"/>.</summary>
    internal static IReadOnlyList<FunctionStat> Diff(IReadOnlyList<FunctionStat> baseline, IReadOnlyList<FunctionStat> current)
    {
        Dictionary<string, (double Total, double Own)> deltas = new(StringComparer.Ordinal);
        foreach (FunctionStat f in current)
            deltas[f.Name] = (f.TotalTime, f.OwnTime);
        foreach (FunctionStat f in baseline)
        {
            (double total, double own) = deltas.GetValueOrDefault(f.Name);
            deltas[f.Name] = (total - f.TotalTime, own - f.OwnTime);
        }

        return [.. deltas.Select(static d => new FunctionStat(d.Key, d.Value.Total, d.Value.Own, null, []))];
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static IEnumerable<FunctionStat> Filter(IEnumerable<FunctionStat> functions, string? include, string? exclude)
    {
        Regex? includeRegex = include is null ? null : new Regex(include, RegexOptions.CultureInvariant);
        Regex? excludeRegex = exclude is null ? null : new Regex(exclude, RegexOptions.CultureInvariant);
        return functions.Where(f => (includeRegex?.IsMatch(f.Name) ?? true) && !(excludeRegex?.IsMatch(f.Name) ?? false));
    }

    private static string Ms(double value) => value.ToString("N1", CultureInfo.InvariantCulture);

    private static string SignedMs(double value) => value.ToString("+#,0.0;-#,0.0;0", CultureInfo.InvariantCulture);

    private static string Percent(double value, double total) => total > 0 ? (value / total).ToString("P1", CultureInfo.InvariantCulture) : "-";

    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
}

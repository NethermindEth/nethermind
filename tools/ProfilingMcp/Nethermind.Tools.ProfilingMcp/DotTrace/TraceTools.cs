// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Nethermind.Tools.ProfilingMcp.DotTrace;

[McpServerToolType]
public sealed class TraceTools(DotTraceSessionManager sessions, Reporter reporter)
{
    private const string ProfilingTypeDescription =
        "Sampling (default; low overhead, accurate time, no call counts), Tracing (exact call counts, higher overhead), " +
        "LineByLine (per-line, highest overhead), or Timeline (thread/GC/lock events over time; cannot be analyzed by trace_hotspots).";

    [McpServerTool(Name = "trace_start", Title = "Start an app under dotTrace", Destructive = false, OpenWorld = false)]
    [Description("Launches a .NET executable under the dotTrace profiler and returns a session id. " +
                 "Let the app run its workload, then call trace_snapshot (or set timeoutSeconds to snapshot automatically when the time is up).")]
    public async Task<string> StartAsync(
        [Description("Path to the executable, or to dotnet.exe with the app's .dll as the first argument.")] string executable,
        [Description("Arguments for the executable.")] string[]? arguments = null,
        [Description("Working directory for the app.")] string? workingDirectory = null,
        [Description(ProfilingTypeDescription)] string profilingType = "Sampling",
        [Description("Collect from launch. Set false to skip startup and call trace_collect with action=start later.")] bool collectFromStart = true,
        [Description("End the session after this many seconds, saving a snapshot and killing the app.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        DotTraceSession session = await sessions.StartAsync(executable, arguments ?? [], workingDirectory, NormalizeType(profilingType), collectFromStart, timeoutSeconds, cancellationToken);
        return FormatSession(session);
    }

    [McpServerTool(Name = "trace_attach", Title = "Attach dotTrace to a process", Destructive = false, OpenWorld = false)]
    [Description("Attaches the dotTrace profiler to a running .NET process (find it with list_dotnet_processes) and returns a session id. " +
                 "On Linux/macOS the profiler is never detached, because detaching crashes the target: trace_stop leaves it loaded but idle, " +
                 "so a process can be attached to only once per run. Take every snapshot you need before trace_stop.")]
    public async Task<string> AttachAsync(
        [Description("Process ID.")] int pid,
        [Description("Sampling (default) or Timeline; attach supports no other types.")] string profilingType = "Sampling",
        [Description("Start collecting immediately. Set false to start later with trace_collect.")] bool collectFromStart = true,
        [Description("Windows only: detach after this many seconds, saving a snapshot.")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        DotTraceSession session = await sessions.AttachAsync(pid, NormalizeType(profilingType), collectFromStart, timeoutSeconds, cancellationToken);
        return FormatSession(session);
    }

    [McpServerTool(Name = "trace_snapshot", Title = "Save a dotTrace snapshot", Destructive = false, OpenWorld = false)]
    [Description("Saves the performance data collected so far to a snapshot file and returns its path for trace_hotspots. " +
                 "Collection stops afterwards unless resume is true.")]
    public async Task<string> SnapshotAsync(
        [Description("Session id from trace_start or trace_attach.")] string sessionId,
        [Description("Keep collecting after the snapshot, e.g. to take a before/after pair.")] bool resume = false,
        [Description("Seconds to wait for the snapshot to be written.")] int waitSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        string snapshot = await sessions.Get(sessionId).TakeSnapshotAsync(resume, TimeSpan.FromSeconds(waitSeconds), cancellationToken);
        return $"Snapshot saved: {snapshot}";
    }

    [McpServerTool(Name = "trace_collect", Title = "Start or discard dotTrace collection", Destructive = false, OpenWorld = false)]
    [Description("action=start begins collecting (after collectFromStart=false or a snapshot); action=drop discards the data collected so far and stops collecting.")]
    public async Task<string> CollectAsync(
        [Description("Session id.")] string sessionId,
        [Description("start or drop.")] string action)
    {
        DotTraceSession session = sessions.Get(sessionId);
        await (action.ToLowerInvariant() switch
        {
            "start" => session.StartCollectingAsync(),
            "drop" => session.DropAsync(),
            _ => throw new ArgumentException("action must be start or drop", nameof(action)),
        });
        return $"Sent {action} to session {sessionId}.";
    }

    [McpServerTool(Name = "trace_stop", Title = "End a dotTrace session", Destructive = true, OpenWorld = false)]
    [Description("Ends a session without taking another snapshot. A process launched by trace_start is killed; an attached process keeps running " +
                 "(on Linux/macOS with the idle profiler still loaded, so it cannot be attached to again until it restarts). Take a snapshot first if you need the data.")]
    public async Task<string> StopAsync([Description("Session id.")] string sessionId)
    {
        DotTraceSession session = sessions.Get(sessionId);
        string summary = FormatSession(session);
        await sessions.RemoveAsync(sessionId);
        return "Stopped.\n" + summary;
    }

    [McpServerTool(Name = "trace_sessions", Title = "List dotTrace sessions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists the profiling sessions with their state, snapshots and recent profiler output.")]
    public string ListSessions()
    {
        IReadOnlyCollection<DotTraceSession> all = sessions.Sessions;
        if (all.Count == 0)
            return "No sessions.";

        StringBuilder sb = new();
        foreach (DotTraceSession session in all)
            sb.Append(FormatSession(session)).Append('\n');
        return sb.ToString();
    }

    [McpServerTool(Name = "trace_hotspots", Title = "Rank methods in a dotTrace snapshot", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Ranks methods in a dotTrace snapshot (*.dtp) by own time (time in the method body) or total time (including callees). Times are milliseconds summed over all threads. " +
                 "Bracketed rows such as [Garbage collection] or [Native or optimized code] are profiler pseudo-frames. Windows only.")]
    public async Task<string> HotspotsAsync(
        [Description("Snapshot index file (*.dtp).")] string snapshot,
        [Description("own or total.")] string sortBy = "own",
        [Description("Number of rows.")] int top = 30,
        [Description("Only methods whose full name matches this regex, e.g. ^Nethermind\\.")] string? include = null,
        [Description("Hide methods whose full name matches this regex, e.g. ^System\\.")] string? exclude = null,
        [Description("Hide methods where threads block (Monitor.Wait, semaphore and socket waits, Thread.Sleep) and compute percentages over the remaining time. " +
                     "Sampling measures wall time, so on a mostly idle process these dominate.")] bool hideWaits = true,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FunctionStat> functions = await reporter.GetAllMethodsAsync(snapshot, cancellationToken);
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
        TextTable table = new("own ms", "own %", "total ms", "samples/calls", "method");
        foreach (FunctionStat f in rows)
            table.Add(Ms(f.OwnTime), Percent(f.OwnTime, basis), Ms(f.TotalTime), Count(f.Calls ?? f.Samples), f.Name);

        string waits = hideWaits
            ? $" Hidden: {Ms(waitOwn)} ms ({Percent(waitOwn, totalOwn)}) blocked in waits; own % is relative to the remaining {Ms(basis)} ms."
            : "";
        string totals = byTotal && hideWaits ? " Total times still include waits in callees." : "";
        return $"{functions.Count} methods; own time over all threads: {Ms(totalOwn)} ms.{waits} Sorted by {(byTotal ? "total" : "own")} time.{totals}\n{table}";
    }

    private static readonly Regex BlockingWait = new(
        @"^(System\.Threading\.(Monitor\.Wait|LowLevelLifoSemaphore\.WaitForSignal|LowLevelMonitor\.Wait|SemaphoreSlim\.(WaitUntilCountOrTimeout|WaitCore)|WaitHandle\.WaitOne\w*|WaitHandle\.WaitMultiple\w*|Thread\.Sleep|ManualResetEventSlim\.Wait)" +
        @"|Interop\+Sys\.(Read|WaitForSocketEvents|Poll)|Interop\+Kernel32\.(GetQueuedCompletionStatus\w*|WaitFor\w+))$",
        RegexOptions.CultureInvariant);

    [McpServerTool(Name = "trace_callstacks", Title = "Show call stacks reaching a method", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows the distinct call stacks (outermost caller first) that reach methods matching a regex, ranked by time, to find which callers make a hotspot expensive. Windows only.")]
    public async Task<string> CallStacksAsync(
        [Description("Snapshot index file (*.dtp).")] string snapshot,
        [Description("Regex on the full method name; escape dots and plus signs, e.g. Nethermind\\.Trie\\.PatriciaTree\\.Get.")] string methodPattern,
        [Description("Maximum stacks to show per method.")] int maxStacks = 10,
        [Description("Maximum frames to show per stack, keeping the innermost.")] int maxFrames = 25,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FunctionStat> functions = await reporter.GetCallStacksAsync(snapshot, methodPattern, cancellationToken);
        if (functions.Count == 0)
            return $"No method matches '{methodPattern}'. Use trace_hotspots to find exact names.";

        StringBuilder sb = new();
        foreach (FunctionStat f in functions.OrderByDescending(f => f.TotalTime).Take(10))
        {
            sb.Append($"{f.Name}: total {Ms(f.TotalTime)} ms, own {Ms(f.OwnTime)} ms, {f.CallStacks.Count} distinct stacks\n");
            foreach (CallStackStat stack in f.CallStacks.OrderByDescending(s => s.TotalTime).Take(maxStacks))
            {
                sb.Append($"  total {Ms(stack.TotalTime)} ms, own {Ms(stack.OwnTime)} ms, samples/calls {Count(stack.Calls ?? stack.Samples)}\n");
                int skipped = Math.Max(0, stack.Frames.Count - maxFrames);
                if (skipped > 0)
                    sb.Append($"    ... {skipped} outer frames\n");
                for (int i = skipped; i < stack.Frames.Count; i++)
                    sb.Append("    ").Append(stack.Frames[i]).Append('\n');
            }
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "trace_report", Title = "Write an HTML report of a dotTrace snapshot", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes a self-contained HTML page for a person to explore a snapshot in a browser: an interactive flame graph of the call tree and a sortable hotspot table, " +
                 "both with search and a blocking-wait filter. Use it when someone asks to see the results; read them yourself with trace_hotspots and trace_callstacks. Windows only.")]
    public async Task<string> ReportAsync(
        [Description("Snapshot index file (*.dtp).")] string snapshot,
        [Description("Where to write the page. Defaults to the snapshot path with .html appended.")] string? output = null,
        [Description("Maximum call-tree nodes in the flame graph; the smallest call paths are dropped to fit.")] int maxNodes = 5000,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FunctionStat> functions = await reporter.GetCallStacksAsync(snapshot, ".*", cancellationToken);
        CallTreeNode tree = CallTree.Build(functions, BlockingWait.IsMatch);
        int nodesBeforePruning = CallTree.Count(tree);
        int kept = CallTree.Prune(tree, maxNodes);

        output ??= snapshot + ".html";
        await File.WriteAllTextAsync(output, HtmlReport.Render(snapshot, functions, tree, nodesBeforePruning, BlockingWait.IsMatch), cancellationToken);
        return $"Report written: {output}\nFlame graph shows {kept:N0} of {nodesBeforePruning:N0} call paths; {Percent(tree.WaitTime, tree.TotalTime)} of sampled time is in blocking waits (hidden by default on the page).";
    }

    [McpServerTool(Name = "trace_compare", Title = "Compare two dotTrace snapshots", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Shows per-method time changes from a baseline snapshot to a current one (current minus baseline), largest first. Use it to verify a fix. " +
                 "Make both snapshots cover the same workload, or the deltas will mostly reflect the difference in run length. Windows only.")]
    public async Task<string> CompareAsync(
        [Description("Snapshot before the change (*.dtp).")] string baseline,
        [Description("Snapshot after the change (*.dtp).")] string current,
        [Description("Number of rows.")] int top = 30,
        [Description("Only methods whose full name matches this regex.")] string? include = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FunctionStat> deltas = await reporter.CompareAsync(baseline, current, cancellationToken);
        TextTable table = new("own Δ ms", "total Δ ms", "method");
        foreach (FunctionStat f in Filter(deltas, include, exclude: null).Where(static f => f.OwnTime != 0 || f.TotalTime != 0).OrderByDescending(static f => Math.Abs(f.OwnTime)).Take(top))
            table.Add(SignedMs(f.OwnTime), SignedMs(f.TotalTime), f.Name);

        return table.Count == 0 ? "No differences." : $"Current minus baseline, sorted by |own Δ|. Negative is faster.\n{table}";
    }

    private static IEnumerable<FunctionStat> Filter(IEnumerable<FunctionStat> functions, string? include, string? exclude)
    {
        Regex? includeRegex = include is null ? null : new Regex(include, RegexOptions.CultureInvariant);
        Regex? excludeRegex = exclude is null ? null : new Regex(exclude, RegexOptions.CultureInvariant);
        return functions.Where(f => (includeRegex?.IsMatch(f.Name) ?? true) && !(excludeRegex?.IsMatch(f.Name) ?? false));
    }

    private static string NormalizeType(string profilingType) => profilingType.ToLowerInvariant() switch
    {
        "sampling" => "Sampling",
        "tracing" => "Tracing",
        "linebyline" or "line-by-line" => "LineByLine",
        "timeline" => "Timeline",
        _ => throw new ArgumentException($"Unknown profiling type '{profilingType}'.", nameof(profilingType)),
    };

    private static string FormatSession(DotTraceSession session)
    {
        StringBuilder sb = new();
        sb.Append($"Session {session.Id}: {session.Description}\n");
        sb.Append($"  type {session.ProfilingType}, state {session.State}");
        if (session.ExitCode is int exitCode)
            sb.Append($" (profiler exit code {exitCode})");
        sb.Append($", profiled PIDs [{string.Join(", ", session.ProfiledPids)}]\n");
        sb.Append($"  snapshot dir {session.SnapshotDir}\n");
        foreach (string snapshot in session.Snapshots)
            sb.Append($"  snapshot {snapshot}\n");
        if (session.State is DotTraceSessionState.Exited || session.ProfiledPids.Count == 0)
            sb.Append(session.FormatOutputTail()).Append('\n');
        return sb.ToString();
    }

    private static string Ms(double value) => value.ToString("N1", CultureInfo.InvariantCulture);

    private static string SignedMs(double value) => value.ToString("+#,0.0;-#,0.0;0", CultureInfo.InvariantCulture);

    private static string Percent(double value, double total) => total > 0 ? (value / total).ToString("P1", CultureInfo.InvariantCulture) : "-";

    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
}

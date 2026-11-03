// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using Nethermind.Tools.ProfilingMcp.DotTrace;
using Nethermind.Tools.ProfilingMcp.Memory;

namespace Nethermind.Tools.ProfilingMcp.EventPipe;

/// <summary>Exceptions of one type and message thrown during a trace.</summary>
/// <param name="ThrowSite">The innermost frame outside the framework (<c>System.*</c>, <c>Microsoft.*</c>, <c>Interop</c>), or the innermost frame when there is none.</param>
public sealed record ExceptionStat(string Type, string Message, int Count, string ThrowSite);

/// <summary>What a <c>.nettrace</c> file contains, in the shape the dotTrace analysis uses.</summary>
/// <param name="Functions">Per-method times in milliseconds with every call path, like a dotTrace report with full call stacks.</param>
/// <param name="Exceptions">Thrown exceptions grouped by type and message, most frequent first.</param>
public sealed record NetTraceProfile(
    IReadOnlyList<FunctionStat> Functions,
    int Threads,
    double DurationMs,
    GcSummary Gc,
    IReadOnlyList<ExceptionStat> Exceptions);

/// <summary>Reads EventPipe traces written by <see cref="CpuSampler"/> or <c>dotnet-trace</c>, caching the result per file.</summary>
public sealed partial class NetTraceReader
{
    private readonly ConcurrentDictionary<string, (DateTime Written, Lazy<NetTraceProfile> Profile)> _cache = new(StringComparer.Ordinal);

    public static bool IsNetTrace(string path) => path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase);

    public NetTraceProfile Read(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Trace not found: {path}");

        DateTime written = File.GetLastWriteTimeUtc(path);
        (DateTime Written, Lazy<NetTraceProfile> Profile) entry = _cache.AddOrUpdate(
            path,
            p => (written, new Lazy<NetTraceProfile>(() => Load(p))),
            (p, existing) => existing.Written == written ? existing : (written, new Lazy<NetTraceProfile>(() => Load(p))));
        return entry.Profile.Value;
    }

    /// <remarks>
    /// Thread times come from TraceEvent's <see cref="SampleProfilerThreadTimeComputer"/>, which <c>dotnet-trace convert</c> also uses.
    /// It ends each stack with a <c>CPU_TIME</c> or <c>UNMANAGED_CODE_TIME</c> pseudo-frame; both are folded into the managed frame
    /// above them, so a thread blocked in native code shows as own time of the wait method that called it, as in dotTrace.
    /// Unresolved frames (<c>?</c>), mostly P/Invoke stubs such as the socket engine's epoll wait, are folded the same way.
    /// </remarks>
    private static NetTraceProfile Load(string path)
    {
        // The index TraceEvent needs is several times larger than the trace and quick to rebuild, so it is not kept.
        string etlx = Path.ChangeExtension(path, $".{Guid.NewGuid():N}.etlx");
        try
        {
            TraceLog.CreateFromEventPipeDataFile(path, etlx, new TraceLogOptions { ContinueOnError = true });
            return Parse(etlx);
        }
        finally
        {
            File.Delete(etlx);
        }
    }

    private static NetTraceProfile Parse(string etlx)
    {
        using TraceLog log = new(etlx);
        using SymbolReader symbols = new(TextWriter.Null) { SymbolPath = "" };
        MutableTraceEventStackSource stacks = new(log) { OnlyManagedCodeStacks = true };
        new SampleProfilerThreadTimeComputer(log, symbols).GenerateThreadTimeStacks(stacks);

        PathNode root = new("", null);
        Dictionary<StackSourceCallStackIndex, PathNode> nodes = [];
        HashSet<string> threads = new(StringComparer.Ordinal);
        stacks.ForEach(sample =>
        {
            PathNode node = Resolve(stacks, sample.StackIndex, root, nodes, threads);
            node.Own += sample.Metric;
            node.Samples++;
        });

        (GcSummary gc, List<ExceptionStat> exceptions) = ReadRuntimeEvents(log);
        return new NetTraceProfile(ToFunctions(root), threads.Count, log.SessionDuration.TotalMilliseconds, gc, exceptions);
    }

    /// <summary>Maps a stack to its node in a tree of cleaned call paths, skipping the computer's grouping and pseudo-frames.</summary>
    private static PathNode Resolve(
        MutableTraceEventStackSource stacks,
        StackSourceCallStackIndex index,
        PathNode root,
        Dictionary<StackSourceCallStackIndex, PathNode> nodes,
        HashSet<string> threads)
    {
        if (index == StackSourceCallStackIndex.Invalid)
            return root;
        if (nodes.TryGetValue(index, out PathNode? cached))
            return cached;

        PathNode caller = Resolve(stacks, stacks.GetCallerIndex(index), root, nodes, threads);
        string frame = stacks.GetFrameName(stacks.GetFrameIndex(index), fullModulePath: false);
        PathNode node;
        if (frame.StartsWith("Thread (", StringComparison.Ordinal))
        {
            threads.Add(frame);
            node = caller;
        }
        else
        {
            string name = CleanFrameName(frame);
            node = IsGrouping(name) ? caller : caller.Child(name);
        }

        nodes[index] = node;
        return node;
    }

    private static bool IsGrouping(string frame) =>
        frame is "CPU_TIME" or "UNMANAGED_CODE_TIME" or "BLOCKED_TIME" or "?" or "Threads" or "(Non-Activities)" or "(Activities)" ||
        frame.StartsWith("Activity ", StringComparison.Ordinal) ||
        frame.StartsWith("Process", StringComparison.Ordinal);

    /// <summary>Turns <c>module!Namespace.Type`1[Arg].Method(args)</c> into <c>Namespace.Type`1.Method</c>, the form dotTrace reports.</summary>
    internal static string CleanFrameName(string frame)
    {
        int bang = frame.IndexOf('!');
        string name = bang >= 0 ? frame[(bang + 1)..] : frame;
        int arguments = name.IndexOf('(');
        if (arguments > 0)
            name = name[..arguments];

        string previous;
        do
        {
            previous = name;
            name = GenericArguments().Replace(name, "");
        }
        while (name != previous);

        return name;
    }

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex GenericArguments();

    private static List<FunctionStat> ToFunctions(PathNode root)
    {
        Dictionary<string, (double Total, double Own, long Samples, List<CallStackStat> Stacks)> functions = new(StringComparer.Ordinal);
        List<string> path = [];

        void Visit(PathNode node)
        {
            path.Add(node.Name);
            (double total, double own, long samples, List<CallStackStat> stacks) = functions.TryGetValue(node.Name, out (double Total, double Own, long Samples, List<CallStackStat> Stacks) f) ? f : (0, 0, 0, []);

            // A recursive method counts its outermost call only, so its total time is not counted twice.
            bool outermost = path.IndexOf(node.Name) == path.Count - 1;
            functions[node.Name] = (outermost ? total + node.Total : total, own + node.Own, outermost ? samples + node.TotalSamples : samples, stacks);
            stacks.Add(new CallStackStat([.. path], node.Total, node.Own, null, node.TotalSamples));

            foreach (PathNode child in node.Children)
                Visit(child);
            path.RemoveAt(path.Count - 1);
        }

        root.ComputeTotals();
        foreach (PathNode child in root.Children)
            Visit(child);

        List<FunctionStat> result = [];
        foreach ((string name, (double total, double own, long samples, List<CallStackStat> stacks)) in functions)
            result.Add(new FunctionStat(name, total, own, null, samples, stacks));
        return result;
    }

    private static (GcSummary Gc, List<ExceptionStat> Exceptions) ReadRuntimeEvents(TraceLog log)
    {
        GcTracker gc = new();
        Dictionary<(string Type, string Message), (int Count, string Site)> exceptions = [];
        TraceLogEventSource source = log.Events.GetSource();
        gc.Subscribe(source);
        source.Clr.ExceptionStart += (ExceptionTraceData data) =>
        {
            (string, string) key = (data.ExceptionType, data.ExceptionMessage);
            exceptions[key] = exceptions.TryGetValue(key, out (int Count, string Site) seen)
                ? (seen.Count + 1, seen.Site)
                : (1, ThrowSite(data.CallStack()));
        };
        source.Process();

        List<ExceptionStat> stats = [];
        foreach (((string type, string message), (int count, string site)) in exceptions)
            stats.Add(new ExceptionStat(type, message, count, site));
        stats.Sort(static (a, b) => b.Count.CompareTo(a.Count));
        return (gc.ToSummary(), stats);
    }

    private static string ThrowSite(TraceCallStack? stack)
    {
        string? innermost = null;
        for (; stack is not null; stack = stack.Caller)
        {
            string name = CleanFrameName(stack.CodeAddress.FullMethodName);
            if (name.Length == 0)
                continue;
            innermost ??= name;
            if (!name.StartsWith("System.", StringComparison.Ordinal) && !name.StartsWith("Microsoft.", StringComparison.Ordinal) && !name.StartsWith("Interop", StringComparison.Ordinal))
                return name;
        }

        return innermost ?? "?";
    }

    private sealed class PathNode(string name, PathNode? parent)
    {
        private readonly Dictionary<string, PathNode> _children = new(StringComparer.Ordinal);

        public string Name { get; } = name;
        public PathNode? Parent { get; } = parent;
        public double Own { get; set; }
        public long Samples { get; set; }
        public double Total { get; private set; }
        public long TotalSamples { get; private set; }
        public IEnumerable<PathNode> Children => _children.Values;

        public PathNode Child(string name)
        {
            if (!_children.TryGetValue(name, out PathNode? child))
                _children[name] = child = new PathNode(name, this);
            return child;
        }

        public void ComputeTotals()
        {
            Total = Own;
            TotalSamples = Samples;
            foreach (PathNode child in _children.Values)
            {
                child.ComputeTotals();
                Total += child.Total;
                TotalSamples += child.TotalSamples;
            }
        }
    }
}

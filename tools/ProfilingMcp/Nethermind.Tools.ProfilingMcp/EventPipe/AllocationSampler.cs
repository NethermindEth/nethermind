// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.Tracing;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Nethermind.Tools.ProfilingMcp.EventPipe;

/// <summary>Allocations sampled from <c>GCAllocationTick</c> events that share a type, allocating method, or call stack.</summary>
/// <param name="SampledBytes">Bytes attributed to the group; the runtime raises one event roughly per 100 KB allocated, so this approximates allocated bytes.</param>
/// <param name="Top">The type or method with the most sampled bytes in the group: the allocating method of a type, the type of a method.</param>
public sealed record AllocationStat(string Name, long Events, long SampledBytes, string Top);

/// <param name="ByType">Allocated types, most bytes first.</param>
/// <param name="BySite">Allocating methods, the innermost frames outside the framework, most bytes first.</param>
/// <param name="Stacks">Call stacks, innermost frame first, most bytes first.</param>
public sealed record AllocationProfile(IReadOnlyList<AllocationStat> ByType, IReadOnlyList<AllocationStat> BySite, IReadOnlyList<(IReadOnlyList<string> Frames, long Events, long SampledBytes)> Stacks);

/// <summary>Records the allocation tick events of a live process with their call stacks, and groups them by type and allocating code.</summary>
internal static class AllocationSampler
{
    /// <summary>GC for the allocation ticks; loader and JIT to name the methods compiled during the session, as the rundown names the others.</summary>
    private const long RuntimeKeywords = (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.Jit);

    private const int MaxStackFrames = 16;

    public static Task CollectAsync(int pid, TimeSpan duration, string output, CancellationToken cancellationToken) =>
        CpuSampler.RecordAsync(pid, [new(ClrTraceEventParser.ProviderName, EventLevel.Verbose, RuntimeKeywords)], duration, output, cancellationToken);

    /// <param name="include">When set, only allocations with a frame matching it are counted.</param>
    public static AllocationProfile Read(string path, Regex? include)
    {
        Dictionary<string, Group> byType = new(StringComparer.Ordinal);
        Dictionary<string, Group> bySite = new(StringComparer.Ordinal);
        Dictionary<string, (string[] Frames, long Events, long Bytes)> byStack = new(StringComparer.Ordinal);

        string etlx = Path.ChangeExtension(path, $".{Guid.NewGuid():N}.etlx");
        try
        {
            TraceLog.CreateFromEventPipeDataFile(path, etlx, new TraceLogOptions { ContinueOnError = true });
            using TraceLog log = new(etlx);
            TraceLogEventSource source = log.Events.GetSource();
            source.Clr.GCAllocationTick += (GCAllocationTickTraceData data) =>
            {
                string[] frames = NetTraceReader.Frames(data.CallStack());
                if (include is not null && !frames.Any(include.IsMatch))
                    return;

                string type = string.IsNullOrEmpty(data.TypeName) ? "?" : data.TypeName;
                string site = NetTraceReader.InnermostApplicationFrame(frames);
                long bytes = data.AllocationAmount64;
                Add(byType, type, site, bytes);
                Add(bySite, site, type, bytes);

                string[] stack = frames.Length > MaxStackFrames ? frames[..MaxStackFrames] : frames;
                string key = string.Join('\n', [type, .. stack]);
                (string[] Frames, long Events, long Bytes) seen = byStack.GetValueOrDefault(key, ([$"new {type}", .. stack], 0, 0));
                byStack[key] = (seen.Frames, seen.Events + 1, seen.Bytes + bytes);
            };
            source.Process();
        }
        finally
        {
            File.Delete(etlx);
        }

        return new AllocationProfile(
            Sorted(byType),
            Sorted(bySite),
            [.. byStack.Values.OrderByDescending(static s => s.Bytes).Select(static s => ((IReadOnlyList<string>)s.Frames, s.Events, s.Bytes))]);
    }

    private static void Add(Dictionary<string, Group> groups, string name, string other, long bytes)
    {
        if (!groups.TryGetValue(name, out Group? group))
            groups[name] = group = new Group();
        group.Events++;
        group.Bytes += bytes;
        group.ByOther[other] = group.ByOther.GetValueOrDefault(other) + bytes;
    }

    private static List<AllocationStat> Sorted(Dictionary<string, Group> groups) =>
        [.. groups
            .Select(static g => new AllocationStat(g.Key, g.Value.Events, g.Value.Bytes, g.Value.ByOther.MaxBy(static o => o.Value).Key))
            .OrderByDescending(static s => s.SampledBytes)];

    private sealed class Group
    {
        public long Events { get; set; }
        public long Bytes { get; set; }
        public Dictionary<string, long> ByOther { get; } = new(StringComparer.Ordinal);
    }
}

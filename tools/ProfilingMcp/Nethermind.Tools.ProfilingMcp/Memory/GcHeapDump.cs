// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Nethermind.Tools.ProfilingMcp.Memory;

/// <summary>A heap read over EventPipe: per-type statistics, and the object graph when references were requested.</summary>
/// <param name="PauseMs">How long the induced GC suspended the process, including the heap walk that reports the objects.</param>
/// <param name="EventsLost">Events the runtime dropped because the session buffer was full; when non-zero the heap is incomplete.</param>
internal sealed record GcHeapDumpResult(HeapSnapshot Snapshot, HeapGraph? Graph, double PauseMs, TimeSpan Elapsed, int EventsLost);

/// <summary>Reads the managed heap of a live process from the runtime's heap-dump events, as <c>dotnet-gcdump</c> does.</summary>
/// <remarks>
/// Enabling the heap-dump keywords makes the runtime run a full blocking GC and report every object that survives it with its type,
/// size and references. Unlike reading the process's memory with ClrMD it needs no ptrace, so it works in containers, and the process
/// is paused only for that GC. The events carry no field names or generations: objects of at least 85,000 bytes are counted as LOH.
/// </remarks>
internal static class GcHeapDump
{
    private const int LargeObjectThreshold = 85_000;

    /// <summary>Same size as <c>dotnet-gcdump</c>'s; the events of a heap of a few GB pass through it while the reader keeps up.</summary>
    private const int BufferMB = 256;

    /// <summary>How long no heap event may arrive after the walk began before the session is stopped, which flushes the rest.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    /// <summary>How long the session may stay silent before the heap read's GC starts; an idle runtime can hold back even its first events.</summary>
    private static readonly TimeSpan QuietBeforeHeap = TimeSpan.FromSeconds(5);

    /// <summary>The longest a read waits for the runtime before stopping the session and judging what arrived.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    /// <summary>How long to wait for another session's heap read of the same process.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);

    /// <remarks>
    /// Reads of one process are serialized across sessions: the runtime sends a heap walk to every session listening, so a second read
    /// started during the first would count its objects twice and leave the other without type names.
    /// </remarks>
    public static async Task<GcHeapDumpResult> CollectAsync(int pid, bool withReferences, CancellationToken cancellationToken)
    {
        using IDisposable exclusive = await ProcessLock.AcquireAsync("heap", pid, LockTimeout, cancellationToken);
        Stopwatch elapsed = Stopwatch.StartNew();
        EventPipeSessionConfiguration configuration = new(
            [new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose, (long)ClrTraceEventParser.Keywords.GCHeapSnapshot)],
            BufferMB,
            requestRundown: false,
            requestStackwalk: false);

        Builder builder = new(withReferences);
        TaskCompletionSource<TraceGC> dumpGc = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int eventsLost;

        DiagnosticsClient client = new(pid);
        using (EventPipeSession session = await client.StartEventPipeSessionAsync(configuration, cancellationToken))
        using (EventPipeEventSource source = new(session.EventStream))
        {
            builder.Subscribe(source.Clr);
            source.NeedLoadedDotNetRuntimes();
            source.AddCallbackOnProcessStart(process => process.AddCallbackOnDotNetRuntimeLoad(runtime => runtime.GCEnd += (_, gc) =>
            {
                // The heap is reported during the induced GC, so the first gen2 GC to end after objects arrived is that one.
                if (gc.Generation == 2 && builder.HasNodes)
                    dumpGc.TrySetResult(gc);
            }));

            Task processing = Task.Run(source.Process, CancellationToken.None);
            try
            {
                // An idle runtime may hold heap events, from the first object to the GC's end, in its buffers; stopping the session flushes
                // them, so a session that has gone quiet is stopped rather than waited on. Whether the heap arrived whole is checked after.
                // Objects are reported only once the GC has marked the heap, which can take longer than any quiet period on a large heap,
                // so between the GC's start and its first object the read waits up to the deadline.
                while (!dumpGc.Task.IsCompleted && !processing.IsCompleted && elapsed.Elapsed < Deadline
                       && (builder.HasNodes ? builder.QuietFor < Quiet : builder.GcStarted || builder.QuietFor < QuietBeforeHeap))
                    await Task.WhenAny(dumpGc.Task, processing, Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)).WaitAsync(cancellationToken);
            }
            finally
            {
                await session.StopAsync(CancellationToken.None);
                await processing;
            }

            eventsLost = source.EventsLost;
        }

        if (!dumpGc.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException(builder.HasNodes
                ? "The heap walk did not finish: the runtime never reported the end of its GC. Try again."
                : $"The runtime reported no heap within {elapsed.Elapsed.TotalSeconds:N0} s: the process ended, or its heap walk was slow to start. Try again.");

        if (builder.UnnamedObjects is > 0 and var unnamed)
            throw new InvalidOperationException($"The heap read is incomplete: {unnamed:N0} objects arrived without their type's name, which happens when " +
                                                "another tool such as dotnet-gcdump reads the heap at the same time. Try again.");

        TraceGC gc = await dumpGc.Task;
        return new GcHeapDumpResult(builder.ToSnapshot(pid, gc), builder.ToGraph(), gc.PauseDurationMSec, elapsed.Elapsed, eventsLost);
    }

    /// <summary>Converts the runtime's type name to ClrMD's form, e.g. <c>Dictionary`2[System.String,System.Int32]</c> to <c>Dictionary&lt;System.String, System.Int32&gt;</c>.</summary>
    /// <remarks>
    /// Keeps <c>heap_diff</c> lined up with snapshots of dumps. The runtime names a nested type without its declaring type
    /// (<c>Entry[...]</c> for a dictionary entry), though generic arguments carry theirs.
    /// </remarks>
    internal static string FormatTypeName(string name)
    {
        StringBuilder formatted = new(name.Length);
        Stack<bool> generic = new();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            switch (c)
            {
                case '`':
                    while (i + 1 < name.Length && char.IsAsciiDigit(name[i + 1]))
                        i++;
                    break;
                case '[':
                    // An array rank such as [] or [,] versus a list of generic arguments.
                    bool isGeneric = i + 1 < name.Length && name[i + 1] is not (']' or ',');
                    generic.Push(isGeneric);
                    formatted.Append(isGeneric ? '<' : '[');
                    break;
                case ']':
                    formatted.Append(generic.TryPop(out bool wasGeneric) && wasGeneric ? '>' : ']');
                    break;
                case ',':
                    formatted.Append(generic.TryPeek(out bool inGeneric) && inGeneric ? ", " : ",");
                    break;
                default:
                    formatted.Append(c);
                    break;
            }
        }

        return formatted.ToString();
    }

    /// <summary>Accumulates the heap-dump events; nodes and edges are recorded only when references are requested.</summary>
    private sealed class Builder(bool withReferences)
    {
        private readonly Dictionary<ulong, int> _typeIndexes = [];
        private readonly List<string> _types = [];
        private readonly List<(long Count, long Bytes, long LargeBytes)> _typeStats = [];
        private readonly List<ulong> _addresses = [];
        private readonly List<int> _nodeTypes = [];
        private readonly List<long> _sizes = [];
        private readonly List<int> _edgeStarts = [0];
        private readonly List<ulong> _edges = [];
        private readonly List<(ulong Address, string Kind)> _roots = [];
        private readonly List<(ulong Key, ulong Value)> _dependentHandles = [];
        private long _objects;
        private long _lastEvent = Environment.TickCount64;
        private volatile bool _gcStarted;
        private bool _inWalkGc;
        private bool _walkRecorded;

        public bool HasNodes => _objects > 0;

        /// <summary>Whether a blocking gen2 GC, which the heap read induces, has started since the session began.</summary>
        public bool GcStarted => _gcStarted;

        /// <summary>Whether objects now arriving belong to the one heap walk this read records.</summary>
        /// <remarks>
        /// The runtime sends every heap walk to every session with the heap-dump keywords, whichever session asked for it, so another
        /// reader such as <c>dotnet-gcdump</c> starting close to this one adds a walk to this session. Only the objects of the first
        /// blocking gen2 GC whose start this session saw are taken: a walk joined midway is skipped and later walks are ignored.
        /// </remarks>
        private bool Recording => _inWalkGc && !_walkRecorded;

        public TimeSpan QuietFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastEvent));

        public long UnnamedObjects
        {
            get
            {
                long unnamed = 0;
                for (int i = 0; i < _types.Count; i++)
                {
                    if (_types[i].StartsWith(UnknownPrefix, StringComparison.Ordinal))
                        unnamed += _typeStats[i].Count;
                }

                return unnamed;
            }
        }

        private const string UnknownPrefix = "<unknown 0x";

        private void Heard() => Interlocked.Exchange(ref _lastEvent, Environment.TickCount64);

        public void Subscribe(ClrTraceEventParser clr)
        {
            clr.GCStart += data =>
            {
                Heard();
                if (data.Depth == 2 && data.Type != GCType.BackgroundGC)
                {
                    _gcStarted = true;
                    _inWalkGc = true;
                }
            };
            clr.GCStop += _ =>
            {
                if (_inWalkGc)
                    _walkRecorded = _objects > 0;
                _inWalkGc = false;
            };
            clr.TypeBulkType += data =>
            {
                Heard();
                for (int i = 0; i < data.Count; i++)
                {
                    GCBulkTypeValues type = data.Values(i);
                    _types[TypeIndex(type.TypeID)] = FormatTypeName(type.TypeName);
                }
            };

            clr.GCBulkNode += data =>
            {
                Heard();
                if (!Recording)
                    return;
                for (int i = 0; i < data.Count; i++)
                {
                    GCBulkNodeValues node = data.Values(i);
                    int type = TypeIndex(node.TypeID);
                    long size = (long)node.Size;
                    (long count, long bytes, long largeBytes) = _typeStats[type];
                    _typeStats[type] = (count + 1, bytes + size, largeBytes + (size >= LargeObjectThreshold ? size : 0));
                    _objects++;

                    if (!withReferences)
                        continue;
                    _addresses.Add(node.Address);
                    _nodeTypes.Add(type);
                    _sizes.Add(size);
                    _edgeStarts.Add(checked(_edgeStarts[^1] + (int)node.EdgeCount));
                }
            };

            if (!withReferences)
                return;

            // Edges arrive in node order: the first EdgeCount of them belong to the first node, and so on.
            clr.GCBulkEdge += data =>
            {
                Heard();
                if (!Recording)
                    return;
                for (int i = 0; i < data.Count; i++)
                    _edges.Add(data.Values(i).Target);
            };
            clr.GCBulkRootEdge += data =>
            {
                if (!Recording)
                    return;
                for (int i = 0; i < data.Count; i++)
                {
                    GCBulkRootEdgeValues root = data.Values(i);
                    if ((root.GCRootFlag & GCRootFlags.WeakRef) != 0)
                        continue;
                    _roots.Add((root.RootedNodeAddress, (root.GCRootFlag & GCRootFlags.Pinning) != 0 ? $"{root.GCRootKind} (pinned)" : root.GCRootKind.ToString()));
                }
            };
            clr.GCBulkRootConditionalWeakTableElementEdge += data =>
            {
                if (!Recording)
                    return;
                for (int i = 0; i < data.Count; i++)
                {
                    GCBulkRootConditionalWeakTableElementEdgeValues edge = data.Values(i);
                    _dependentHandles.Add((edge.GCKeyNodeID, edge.GCValueNodeID));
                }
            };
        }

        public HeapSnapshot ToSnapshot(int pid, TraceGC gc)
        {
            List<string> names = _types;
            long totalBytes = 0;
            List<TypeStat> types = [];
            for (int i = 0; i < names.Count; i++)
            {
                (long count, long bytes, long largeBytes) = _typeStats[i];
                if (count == 0)
                    continue;
                types.Add(new TypeStat(names[i], count, bytes, largeBytes));
                totalBytes += bytes;
            }

            Dictionary<string, long> generations = [];
            long freeBytes = 0;
            if (gc.HeapStats is { } heap)
            {
                generations["gen0"] = heap.GenerationSize0;
                generations["gen1"] = heap.GenerationSize1;
                generations["gen2"] = heap.GenerationSize2;
                generations["LOH"] = heap.GenerationSize3;
                generations["POH"] = heap.GenerationSize4;
            }

            if (gc.PerHeapHistories is { Count: > 0 })
            {
                foreach (Gens generation in (Gens[])[Gens.Gen0, Gens.Gen1, Gens.Gen2, Gens.GenLargeObj, Gens.GenPinObj])
                    freeBytes += (long)(gc.GenFragmentationMB(generation) * (1 << 20));
            }

            return new HeapSnapshot(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTime.UtcNow, _objects, totalBytes, freeBytes, generations,
                HeapSnapshot.MergeByName(types));
        }

        public HeapGraph? ToGraph() => withReferences
            ? new HeapGraph([.. _types], [.. _nodeTypes], [.. _addresses], [.. _sizes], [.. _edgeStarts], _edges, _roots, _dependentHandles)
            : null;

        private int TypeIndex(ulong typeId)
        {
            if (!_typeIndexes.TryGetValue(typeId, out int index))
            {
                _typeIndexes[typeId] = index = _types.Count;
                _types.Add($"{UnknownPrefix}{typeId:x}>");
                _typeStats.Add((0, 0, 0));
            }

            return index;
        }
    }
}

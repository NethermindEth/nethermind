// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Attributes;

namespace Nethermind.Trie.Native;

/// <summary>Process-wide counters of native bulk sets and their <see cref="NativeNodeBuffer"/>, read from the native library.</summary>
/// <remarks>All zero when the native library is not available.</remarks>
public static partial class Metrics
{
    [GaugeMetric]
    [Description("Bytes of the native trie node buffer generations allocated, live or still pinned by a running bulk set")]
    public static long NodeRecordBufferMemory => (long)Read().BufferMemory;

    [GaugeMetric]
    [Description("Native trie node buffer generations allocated, live or still pinned by a running bulk set")]
    public static long NodeRecordBufferGenerations => (long)Read().BufferGenerations;

    [GaugeMetric]
    [Description("Bytes of what compaction kept of dropped native trie node buffer generations")]
    public static long NodeRecordBufferCompressedMemory => (long)Read().CompressedMemory;

    [GaugeMetric]
    [Description("Compacted native trie node buffer generations of which something is kept")]
    public static long NodeRecordBufferCompressedGenerations => (long)Read().CompressedGenerations;

    [CounterMetric]
    [Description("Native bulk sets on a non-empty trie whose root was in the native trie node buffer")]
    public static long NodeRecordBufferRootHits => (long)Read().RootHits;

    [CounterMetric]
    [Description("Native bulk sets on a non-empty trie whose root was not in the native trie node buffer")]
    public static long NodeRecordBufferRootMisses => (long)Read().RootMisses;

    [CounterMetric]
    [Description("Trie nodes native bulk sets read in place from the native trie node buffer")]
    public static long NativeBulkSetNodesFromBuffer => (long)Read().NodesFromBuffer;

    [CounterMetric]
    [Description("Trie nodes native bulk sets read back from the nodes pending publication")]
    public static long NativeBulkSetNodesFromPending => (long)Read().NodesFromPending;

    [CounterMetric]
    [Description("Trie nodes native bulk sets fetched from the node store")]
    public static long NativeBulkSetNodesFetched => (long)Read().NodesFetched;

    [CounterMetric]
    [Description("Time spent in native bulk sets over managed entries, converting the entries and calling the native library (ticks)")]
    public static long NativeBulkSetTime => Volatile.Read(ref _bulkSetTime);
    private static long _bulkSetTime;

    [CounterMetric]
    [Description("Time spent in the native bulk set call alone, node store callbacks included (ticks)")]
    public static long NativeBulkSetCallTime => Volatile.Read(ref _bulkSetCallTime);
    private static long _bulkSetCallTime;

    [CounterMetric]
    [Description("Time spent in the managed node store callbacks of native bulk set calls, summed over the threads calling back, native decoding of the nodes they hand over included (ticks)")]
    public static long NativeBulkSetFetchTime => Volatile.Read(ref _bulkSetFetchTime);
    private static long _bulkSetFetchTime;

    internal static void AddBulkSet(long ticks) => Interlocked.Add(ref _bulkSetTime, ticks);

    internal static void AddBulkSetCall(long callTicks, long fetchTicks)
    {
        Interlocked.Add(ref _bulkSetCallTime, callTicks);
        Interlocked.Add(ref _bulkSetFetchTime, fetchTicks);
    }

    private static unsafe NativeMetrics Read()
    {
        NativeMetrics metrics = default;
        if (NativePatriciaBulkSet.IsAvailable) nm_patricia_metrics(&metrics);
        return metrics;
    }

    [LibraryImport(NativePatriciaBulkSet.LibraryName)]
    private static unsafe partial void nm_patricia_metrics(NativeMetrics* metrics);

    // Laid out as the native Metrics.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMetrics
    {
        public readonly ulong BufferMemory;
        public readonly ulong BufferGenerations;
        public readonly ulong RootHits;
        public readonly ulong RootMisses;
        public readonly ulong NodesFromBuffer;
        public readonly ulong NodesFromPending;
        public readonly ulong NodesFetched;
        public readonly ulong CompressedMemory;
        public readonly ulong CompressedGenerations;
    }
}

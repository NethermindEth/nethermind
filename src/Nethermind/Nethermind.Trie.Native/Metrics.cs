// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.InteropServices;
using Nethermind.Core.Attributes;

namespace Nethermind.Trie.Native;

/// <summary>Process-wide counters of native bulk sets and their <see cref="NativeTrieNodeLog"/>, read from the native library.</summary>
/// <remarks>All zero when the native library is not available.</remarks>
public static partial class Metrics
{
    [GaugeMetric]
    [Description("Bytes of the native trie node log generations allocated, live or still pinned by a running bulk set")]
    public static long NativeTrieNodeLogMemory => (long)Read().LogMemory;

    [GaugeMetric]
    [Description("Native trie node log generations allocated, live or still pinned by a running bulk set")]
    public static long NativeTrieNodeLogGenerations => (long)Read().LogGenerations;

    [CounterMetric]
    [Description("Native bulk sets on a non-empty trie whose root was in the native trie node log")]
    public static long NativeTrieNodeLogRootHits => (long)Read().RootHits;

    [CounterMetric]
    [Description("Native bulk sets on a non-empty trie whose root was not in the native trie node log")]
    public static long NativeTrieNodeLogRootMisses => (long)Read().RootMisses;

    [CounterMetric]
    [Description("Trie nodes native bulk sets read in place from the native trie node log")]
    public static long NativeBulkSetNodesFromLog => (long)Read().NodesFromLog;

    [CounterMetric]
    [Description("Trie nodes native bulk sets read back from the nodes pending publication")]
    public static long NativeBulkSetNodesFromPending => (long)Read().NodesFromPending;

    [CounterMetric]
    [Description("Trie nodes native bulk sets fetched from the node store")]
    public static long NativeBulkSetNodesFetched => (long)Read().NodesFetched;

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
        public readonly ulong LogMemory;
        public readonly ulong LogGenerations;
        public readonly ulong RootHits;
        public readonly ulong RootMisses;
        public readonly ulong NodesFromLog;
        public readonly ulong NodesFromPending;
        public readonly ulong NodesFetched;
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
[assembly: InternalsVisibleTo("Nethermind.State")]
[assembly: InternalsVisibleTo("Nethermind.Evm")]
[assembly: InternalsVisibleTo("Nethermind.TxPool")]
namespace Nethermind.Db
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of Code DB cache reads.")]
        public static long CodeDbCache => _codeDbCache.GetTotalValue();
        private static readonly ZeroContentionCounter _codeDbCache = new();
        [Description("Number of Code DB cache reads on thread.")]
        internal static long ThreadLocalCodeDbCache => _codeDbCache.ThreadLocalValue;
        internal static void IncrementCodeDbCache() => _codeDbCache.Increment();

        [CounterMetric]
        [Description("Number of State Trie cache hits.")]
        public static long StateTreeCache => _stateTreeCacheHits.GetTotalValue();
        private static readonly ZeroContentionCounter _stateTreeCacheHits = new();
        internal static void IncrementStateTreeCacheHits() => _stateTreeCacheHits.Increment();

        [CounterMetric]
        [Description("Number of State Trie reads.")]
        public static long StateTreeReads => _stateTreeReads.GetTotalValue();
        private static readonly ZeroContentionCounter _stateTreeReads = new();

        [Description("Number of State Trie reads on thread.")]
        internal static long ThreadLocalStateTreeReads => _stateTreeReads.ThreadLocalValue;
        internal static void IncrementStateTreeReads() => _stateTreeReads.Increment();

        [CounterMetric]
        [Description("Number of State Reader reads.")]
        public static long StateReaderReads => _stateReaderReads.GetTotalValue();
        private static readonly ZeroContentionCounter _stateReaderReads = new();
        internal static void IncrementStateReaderReads() => _stateReaderReads.Increment();

        [CounterMetric]
        [Description("Number of state trie writes.")]
        public static long StateTreeWrites => _stateTreeWrites;
        private static long _stateTreeWrites;
        internal static void IncrementStateTreeWrites(long value) => Interlocked.Add(ref _stateTreeWrites, value);

        [CounterMetric]
        [Description("Number of state trie writes skipped in net.")]
        public static long StateSkippedWrites => _stateSkippedWrites;
        private static long _stateSkippedWrites;
        internal static void IncrementStateSkippedWrites(long value) => Interlocked.Add(ref _stateSkippedWrites, value);

        [CounterMetric]
        [Description("Number of State DB duplicate writes during full pruning.")]
        public static int StateDbInPruningWrites;

        [CounterMetric]
        [Description("Number of storage trie cache hits.")]
        public static long StorageTreeCache => _storageTreeCache.GetTotalValue();
        private static readonly ZeroContentionCounter _storageTreeCache = new();
        internal static void IncrementStorageTreeCache() => _storageTreeCache.Increment();

        [CounterMetric]
        [Description("Number of storage trie reads.")]
        public static long StorageTreeReads => _storageTreeReads.GetTotalValue();
        private static readonly ZeroContentionCounter _storageTreeReads = new();

        [Description("Number of storage trie reads on thread.")]
        internal static long ThreadLocalStorageTreeReads => _storageTreeReads.ThreadLocalValue;
        internal static void IncrementStorageTreeReads() => _storageTreeReads.Increment();

        [CounterMetric]
        [Description("Number of storage reader reads.")]
        public static long StorageReaderReads { get; set; }

        [CounterMetric]
        [Description("Number of storage trie writes.")]
        public static long StorageTreeWrites => _storageTreeWrites;
        private static long _storageTreeWrites;
        internal static void IncrementStorageTreeWrites(long value) => Interlocked.Add(ref _storageTreeWrites, value);

        [CounterMetric]
        [Description("Number of storage trie writes skipped in net.")]
        public static long StorageSkippedWrites => _storageSkippedWrites;
        private static long _storageSkippedWrites;
        internal static void IncrementStorageSkippedWrites(long value) => Interlocked.Add(ref _storageSkippedWrites, value);

        [GaugeMetric]
        [Description("Indicator if StadeDb is being pruned.")]
        public static int StateDbPruning { get; set; }

        [GaugeMetric]
        [Description("Database reads per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbReads { get; } = new();

        [GaugeMetric]
        [Description("Database writes per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbWrites { get; } = new();

        [GaugeMetric]
        [Description("Database size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbSize { get; } = new();

        [GaugeMetric]
        [Description("Database memtable per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbMemtableSize { get; } = new();

        [GaugeMetric]
        [Description("Database block cache size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbBlockCacheSize { get; } = new();

        [GaugeMetric]
        [Description("Database index and filter size per database")]
        [KeyIsLabel("db")]
        public static NonBlocking.ConcurrentDictionary<string, long> DbIndexFilterSize { get; } = new();

        [Description("Metrics extracted from RocksDB Compaction Stats and DB Statistics")]
        [KeyIsLabel("db", "metric")]
        public static NonBlocking.ConcurrentDictionary<(string, string), double> DbStats { get; } = new();

        [Description("Metrics extracted from RocksDB Compaction Stats")]
        [KeyIsLabel("db", "level", "metric")]
        public static NonBlocking.ConcurrentDictionary<(string, int, string), double> DbCompactionStats { get; } = new();
    }
}

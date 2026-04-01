// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
[Config(typeof(Config))]
public class PatriciaTreeBulkReadBenchmarks
{
    private const int TreeSize = 16384;
    private const int BackgroundSize = 100_000;
    private const int TotalSize = TreeSize + BackgroundSize;

    internal static readonly ConcurrentDictionary<string, ConcurrentBag<double>> HitRates = new();

    private BlockCacheTrieStore _blockCacheStore = null!;
    private Hash256 _rootHash = null!;
    private ValueHash256[] _readKeys = null!;
    private ValueHash256[] _sorted_0_1 = null!;
    private ValueHash256[] _sorted_1_2 = null!;
    private ValueHash256[] _sorted_2_3 = null!;
    private ValueHash256[] _sorted_0_2 = null!;
    private ValueHash256[] _sorted_0_3 = null!;
    private ValueHash256[] _sorted_1_3 = null!;

    [Params(16, 64, 256, 1024, 4096, 8192, 16384)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        TestMemDb db = new();
        RawScopedTrieStore baseStore = new(db);

        PatriciaTree tree = new(baseStore, LimboLogs.Instance);

        Random rng = new(42);
        ValueHash256[] allKeys = new ValueHash256[TotalSize];

        // Build tree with TreeSize + BackgroundSize entries
        // Only the first TreeSize keys will be read; the rest dilute the block cache
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(TotalSize);
        for (int i = 0; i < TotalSize; i++)
        {
            byte[] keyBuffer = new byte[32];
            rng.NextBytes(keyBuffer);
            allKeys[i] = new ValueHash256(keyBuffer);

            byte[] valueBuffer = new byte[32];
            rng.NextBytes(valueBuffer);
            entries.Add(new PatriciaTree.BulkSetEntry(in allKeys[i], valueBuffer));
        }

        tree.BulkSet(entries);
        tree.UpdateRootHash();
        _rootHash = tree.RootHash;
        tree.Commit();

        List<(TreePath path, Hash256 hash)> nodeList = [];
        CollectNodes(baseStore, tree.RootRef!, TreePath.Empty, nodeList);
        nodeList.Sort((a, b) => a.path.CompareTo(b.path));

        Dictionary<Hash256, int> pathIndex = new(nodeList.Count);
        for (int i = 0; i < nodeList.Count; i++)
            pathIndex[nodeList[i].hash] = i;

        _blockCacheStore = new BlockCacheTrieStore(baseStore, pathIndex);

        _readKeys = new ValueHash256[KeyCount];
        Random readRng = new(123);
        for (int i = 0; i < KeyCount; i++)
            _readKeys[i] = allKeys[readRng.Next(TreeSize)];

        // Pre-sort all variants in GlobalSetup so sort cost is excluded from timing
        _sorted_0_1 = RadixSortKeys(_readKeys, 0, 1);
        _sorted_1_2 = RadixSortKeys(_readKeys, 1, 2);
        _sorted_2_3 = RadixSortKeys(_readKeys, 2, 3);
        _sorted_0_2 = RadixSortKeys(_readKeys, 0, 2);
        _sorted_0_3 = RadixSortKeys(_readKeys, 0, 3);
        _sorted_1_3 = RadixSortKeys(_readKeys, 1, 3);
    }

    private static ValueHash256[] RadixSortKeys(ValueHash256[] keys, int startByte, int endByte)
    {
        int len = keys.Length;
        ValueHash256[] buf0 = new ValueHash256[len];
        ValueHash256[] buf1 = new ValueHash256[len];
        keys.CopyTo(buf0.AsSpan());

        Span<int> counts = stackalloc int[256];

        bool flipped = false;
        for (int p = endByte; p >= startByte; p--)
        {
            if (!flipped)
                RadixPass(buf0.AsSpan(), buf1.AsSpan(), len, p, counts);
            else
                RadixPass(buf1.AsSpan(), buf0.AsSpan(), len, p, counts);
            flipped = !flipped;
        }

        return flipped ? buf1 : buf0;
    }

    private static void CollectNodes(IScopedTrieStore store, TrieNode node, TreePath path, List<(TreePath, Hash256)> result)
    {
        node.ResolveNode(store, path);
        if (node.Keccak is Hash256 keccak)
            result.Add((path, keccak));

        if (node.IsBranch)
        {
            for (int i = 0; i < TrieNode.BranchesCount; i++)
            {
                TreePath childPath = path.Append(i);
                TrieNode child = node.GetChildWithChildPath(store, ref childPath, i);
                if (child is not null)
                    CollectNodes(store, child, childPath, result);
            }
        }
        else if (node.IsExtension)
        {
            TreePath childPath = path;
            childPath.AppendMut(node.Key);
            TrieNode child = node.GetChildWithChildPath(store, ref childPath, 0);
            if (child is not null)
                CollectNodes(store, child, childPath, result);
        }
    }

    private void RecordHitRate(string method)
    {
        (long hits, long misses) = _blockCacheStore.GetAndResetStats();
        long total = hits + misses;
        if (total > 0)
        {
            string key = $"{method} | KeyCount={KeyCount}";
            HitRates.GetOrAdd(key, _ => new ConcurrentBag<double>()).Add(100.0 * hits / total);
        }
    }

    [Benchmark(Baseline = true)]
    public void ReadOneByOne()
    {
        _blockCacheStore.ResetBlockCache();
        PatriciaTree tree = new(_blockCacheStore, _rootHash, true, LimboLogs.Instance);
        for (int i = 0; i < _readKeys.Length; i++)
            tree.Get(_readKeys[i].Bytes);
        RecordHitRate(nameof(ReadOneByOne));
    }

    [Benchmark]
    public void ParallelNoSort() => SortAndParallelRead(0, 0, nameof(ParallelNoSort));

    [Benchmark]
    public void ParallelRadix_0_1() => SortAndParallelRead(0, 1, nameof(ParallelRadix_0_1));

    [Benchmark]
    public void ParallelRadix_1_2() => SortAndParallelRead(1, 2, nameof(ParallelRadix_1_2));

    [Benchmark]
    public void ParallelRadix_2_3() => SortAndParallelRead(2, 3, nameof(ParallelRadix_2_3));

    [Benchmark]
    public void ParallelRadix_0_2() => SortAndParallelRead(0, 2, nameof(ParallelRadix_0_2));

    [Benchmark]
    public void ParallelRadix_0_3() => SortAndParallelRead(0, 3, nameof(ParallelRadix_0_3));

    [Benchmark]
    public void ParallelRadix_1_3() => SortAndParallelRead(1, 3, nameof(ParallelRadix_1_3));

    [Benchmark]
    public void BulkRead()
    {
        _blockCacheStore.ResetBlockCache();
        TrieNode root = _blockCacheStore.FindCachedOrUnknown(TreePath.Empty, _rootHash);
        NoOpSink sink = default;
        PatriciaTrieBulkReader.BulkRead(_blockCacheStore, root, _readKeys, ref sink);
        RecordHitRate(nameof(BulkRead));
    }

    private struct NoOpSink : IPatriciaTrieBulkReaderSink<NoOpSink>
    {
        public void OnRead(in ValueHash256 key, int idx, ReadOnlySpan<byte> value) { }
    }

    private void SortAndParallelRead(int startByte, int endByte, string method)
    {
        _blockCacheStore.ResetBlockCache();

        int len = _readKeys.Length;
        using ArrayPoolList<ValueHash256> buf0 = new(len, len);
        using ArrayPoolList<ValueHash256> buf1 = new(len, len);
        _readKeys.CopyTo(buf0.AsSpan());

        Span<int> counts = stackalloc int[256];
        bool flipped = false;
        for (int p = endByte; p >= startByte; p--)
        {
            if (!flipped)
                RadixPass(buf0.AsSpan(), buf1.AsSpan(), len, p, counts);
            else
                RadixPass(buf1.AsSpan(), buf0.AsSpan(), len, p, counts);
            flipped = !flipped;
        }

        ValueHash256[] keys = flipped ? buf1.UnsafeGetInternalArray() : buf0.UnsafeGetInternalArray();
        PatriciaTree tree = new(_blockCacheStore, _rootHash, true, LimboLogs.Instance);
        Parallel.For(0, len, (i) =>
        {
            tree.Get(keys[i].Bytes);
        });
        RecordHitRate(method);
    }

    private static void RadixPass(Span<ValueHash256> src, Span<ValueHash256> dst, int len, int byteIndex, Span<int> counts)
    {
        counts.Clear();
        for (int i = 0; i < len; i++)
            counts[src[i].BytesAsSpan[byteIndex]]++;

        int total = 0;
        for (int b = 0; b < 256; b++)
        {
            int c = counts[b];
            counts[b] = total;
            total += c;
        }

        for (int i = 0; i < len; i++)
        {
            byte key = src[i].BytesAsSpan[byteIndex];
            dst[counts[key]++] = src[i];
        }
    }

    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Default));
            AddColumn(new CacheHitRateColumn());
        }
    }

    private class CacheHitRateColumn : IColumn
    {
        public string Id => "CacheHit%";
        public string ColumnName => "CacheHit%";
        public string Legend => "Block cache hit rate (%)";
        public bool AlwaysShow => true;
        public ColumnCategory Category => ColumnCategory.Custom;
        public int PriorityInCategory => 0;
        public bool IsNumeric => true;
        public UnitType UnitType => UnitType.Dimensionless;
        public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;
        public bool IsAvailable(Summary summary) => true;

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
        {
            string method = benchmarkCase.Descriptor.WorkloadMethod.Name;
            object keyCountParam = benchmarkCase.Parameters["KeyCount"];
            string key = $"{method} | KeyCount={keyCountParam}";

            if (HitRates.TryGetValue(key, out ConcurrentBag<double> rates) && rates.Count > 0)
                return $"{rates.Average():F1}%";

            return "N/A";
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
            GetValue(summary, benchmarkCase);
    }

    /// <summary>
    /// Models OS block cache with LRU eviction. Nodes are ordered on disk by TreePath.
    /// Node index / BlockSize = block ID. A ConcurrentDictionary tracks recently accessed blocks.
    /// If the block is cached, it's a hit (no delay). Otherwise, delay + cache the block.
    /// </summary>
    internal sealed class BlockCacheTrieStore(IScopedTrieStore inner, Dictionary<Hash256, int> pathIndex) : IScopedTrieStore
    {
        private const int SpinIterations = 5000; // ~100µs I/O latency on cache miss
        private const int LruSize = 400;
        private const int BlockSize = 5;

        private readonly ConcurrentDictionary<Hash256, TrieNode> _nodeCache = new();
        private readonly ConcurrentDictionary<int, long> _blockCache = new();
        private long _accessCounter;
        private long _hits;
        private long _misses;

        public void ResetBlockCache()
        {
            _blockCache.Clear();
            _nodeCache.Clear();
            Interlocked.Exchange(ref _accessCounter, 0);
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _misses, 0);
        }

        public (long hits, long misses) GetAndResetStats()
        {
            long h = Interlocked.Exchange(ref _hits, 0);
            long m = Interlocked.Exchange(ref _misses, 0);
            return (h, m);
        }

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            _nodeCache.GetOrAdd(hash, static h => new TrieNode(NodeType.Unknown, h));

        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            SimulateBlockCache(hash);
            return inner.LoadRlp(in path, hash, flags);
        }

        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            SimulateBlockCache(hash);
            return inner.TryLoadRlp(in path, hash, flags);
        }

        private void SimulateBlockCache(Hash256 hash)
        {
            if (!pathIndex.TryGetValue(hash, out int index))
            {
                Interlocked.Increment(ref _misses);
                Thread.SpinWait(SpinIterations);
                return;
            }

            int blockId = index / BlockSize;
            long now = Interlocked.Increment(ref _accessCounter);

            if (_blockCache.TryGetValue(blockId, out _))
            {
                _blockCache[blockId] = now;
                Interlocked.Increment(ref _hits);
                return;
            }

            Interlocked.Increment(ref _misses);
            Thread.SpinWait(SpinIterations);

            _blockCache[blockId] = now;

            if (_blockCache.Count > LruSize)
            {
                long threshold = now - LruSize * 2;
                foreach (var kvp in _blockCache)
                {
                    if (kvp.Value < threshold)
                        _blockCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => inner.GetStorageTrieNodeResolver(address);
        public INodeStorage.KeyScheme Scheme => inner.Scheme;
        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);
    }
}

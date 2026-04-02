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
using Nethermind.Core.Caching;
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
    private const int BackgroundSize = 400_000;
    private const int TotalSize = TreeSize + BackgroundSize;

    internal static readonly ConcurrentDictionary<string, ConcurrentBag<double>> HitRates = new();
    internal static readonly ConcurrentDictionary<string, ConcurrentBag<long>> NodeGets = new();
    internal static readonly ConcurrentDictionary<string, ConcurrentBag<long>> NodeHits = new();
    internal static readonly ConcurrentDictionary<string, ConcurrentBag<long>> RlpGets = new();
    internal static readonly ConcurrentDictionary<string, ConcurrentBag<long>> RlpCacheHits = new();

    private BlockCacheTrieStore _blockCacheStore = null!;
    private Hash256 _rootHash = null!;
    private ValueHash256[] _readKeys = null!;

    [Params(256, 4096, 16384)]
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

        Dictionary<TreePath, int> pathIndex = new(nodeList.Count);
        for (int i = 0; i < nodeList.Count; i++)
            pathIndex[nodeList[i].path] = i;

        _blockCacheStore = new BlockCacheTrieStore(baseStore, pathIndex);

        _readKeys = new ValueHash256[KeyCount];
        Random readRng = new(123);
        for (int i = 0; i < KeyCount; i++)
            _readKeys[i] = allKeys[readRng.Next(TreeSize)];
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
        var stats = _blockCacheStore.GetAndResetStats();
        string key = $"{method} | KeyCount={KeyCount}";
        long rlpTotal = stats.RlpHits + stats.RlpMisses;
        if (rlpTotal > 0)
            HitRates.GetOrAdd(key, _ => new ConcurrentBag<double>()).Add(100.0 * stats.RlpHits / rlpTotal);
        NodeGets.GetOrAdd(key, _ => new ConcurrentBag<long>()).Add(stats.NodeGets);
        NodeHits.GetOrAdd(key, _ => new ConcurrentBag<long>()).Add(stats.NodeHits);
        RlpGets.GetOrAdd(key, _ => new ConcurrentBag<long>()).Add(rlpTotal);
        RlpCacheHits.GetOrAdd(key, _ => new ConcurrentBag<long>()).Add(stats.RlpHits);
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
    public void SequentialRadix_0_8()
    {
        _blockCacheStore.ResetBlockCache();

        int len = _readKeys.Length;
        using ArrayPoolList<ValueHash256> buf0 = new(len, len);
        using ArrayPoolList<ValueHash256> buf1 = new(len, len);
        _readKeys.CopyTo(buf0.AsSpan());

        Span<int> counts = stackalloc int[256];
        bool flipped = false;
        for (int p = 8; p >= 0; p--)
        {
            if (!flipped)
                RadixPass(buf0.AsSpan(), buf1.AsSpan(), len, p, counts);
            else
                RadixPass(buf1.AsSpan(), buf0.AsSpan(), len, p, counts);
            flipped = !flipped;
        }

        ValueHash256[] keys = flipped ? buf1.UnsafeGetInternalArray() : buf0.UnsafeGetInternalArray();
        PatriciaTree tree = new(_blockCacheStore, _rootHash, true, LimboLogs.Instance);
        for (int i = 0; i < len; i++)
            tree.Get(keys[i].Bytes);
        RecordHitRate(nameof(SequentialRadix_0_8));
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
            AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Default)
                .WithIterationCount(3).WithWarmupCount(1));
            AddColumn(new CacheHitRateColumn());
            AddColumn(new LongStatColumn("NodeGet", "FindCachedOrUnknown calls", NodeGets));
            AddColumn(new LongStatColumn("NodeHit", "FindCachedOrUnknown cache hits", NodeHits));
            AddColumn(new LongStatColumn("RlpGet", "LoadRlp calls", RlpGets));
            AddColumn(new LongStatColumn("RlpHit", "LoadRlp block cache hits", RlpCacheHits));
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

    private class LongStatColumn(string name, string legend, ConcurrentDictionary<string, ConcurrentBag<long>> store) : IColumn
    {
        public string Id => name;
        public string ColumnName => name;
        public string Legend => legend;
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
            return store.TryGetValue(key, out var values) && values.Count > 0
                ? $"{(long)values.Average():N0}"
                : "N/A";
        }

        public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style) =>
            GetValue(summary, benchmarkCase);
    }

    /// <summary>
    /// Models OS block cache with LRU eviction. Nodes are ordered on disk by TreePath.
    /// Node index / BlockSize = block ID. A ConcurrentDictionary tracks recently accessed blocks.
    /// If the block is cached, it's a hit (no delay). Otherwise, delay + cache the block.
    /// </summary>
    internal sealed class BlockCacheTrieStore(IScopedTrieStore inner, Dictionary<TreePath, int> pathIndex) : IScopedTrieStore
    {
        private const int SpinIterations = 5000; // ~100µs I/O latency on cache miss
        private const int LruSize = 416;
        private const int BlockSize = 24;
        private const int ShardCount = 16;

        private readonly ConcurrentDictionary<Hash256, TrieNode> _nodeCache = new();
        private readonly LruKeyCache<int>[] _shards = CreateShards();
        private long _hits;
        private long _misses;
        private long _nodeGets;
        private long _nodeHits;

        private static LruKeyCache<int>[] CreateShards()
        {
            LruKeyCache<int>[] shards = new LruKeyCache<int>[ShardCount];
            for (int i = 0; i < ShardCount; i++)
                shards[i] = new LruKeyCache<int>(LruSize / ShardCount, $"BlockCache_{i}");
            return shards;
        }

        public void ResetBlockCache()
        {
            for (int i = 0; i < ShardCount; i++)
                _shards[i].Clear();
            _nodeCache.Clear();
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _misses, 0);
            Interlocked.Exchange(ref _nodeGets, 0);
            Interlocked.Exchange(ref _nodeHits, 0);
        }

        public (long RlpHits, long RlpMisses, long NodeGets, long NodeHits) GetAndResetStats()
        {
            long h = Interlocked.Exchange(ref _hits, 0);
            long m = Interlocked.Exchange(ref _misses, 0);
            long ng = Interlocked.Exchange(ref _nodeGets, 0);
            long nh = Interlocked.Exchange(ref _nodeHits, 0);
            return (h, m, ng, nh);
        }

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            Interlocked.Increment(ref _nodeGets);
            bool existed = _nodeCache.TryGetValue(hash, out TrieNode node);
            if (existed)
            {
                Interlocked.Increment(ref _nodeHits);
                return node!;
            }
            return _nodeCache.GetOrAdd(hash, static h => new TrieNode(NodeType.Unknown, h));
        }

        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            SimulateBlockCache(in path);
            return inner.LoadRlp(in path, hash, flags);
        }

        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            SimulateBlockCache(in path);
            return inner.TryLoadRlp(in path, hash, flags);
        }

        private void SimulateBlockCache(in TreePath path)
        {
            if (!pathIndex.TryGetValue(path, out int index))
                throw new InvalidOperationException($"Unknown path: {path}");

            int blockId = index / BlockSize;
            LruKeyCache<int> shard = _shards[(uint)blockId % ShardCount];

            if (shard.Get(blockId))
            {
                Interlocked.Increment(ref _hits);
                return;
            }

            Interlocked.Increment(ref _misses);
            Thread.SpinWait(SpinIterations);
            shard.Set(blockId);
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => inner.GetStorageTrieNodeResolver(address);
        public INodeStorage.KeyScheme Scheme => inner.Scheme;
        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);
    }
}

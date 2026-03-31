// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
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
[SimpleJob(iterationCount: 16)]
public class PatriciaTreeBulkReadBenchmarks
{
    private const int TreeSize = 16384;

    private IScopedTrieStore _trieStore = null!;
    private Hash256 _rootHash = null!;
    private ValueHash256[] _readKeys = null!;

    [Params(16, 64, 256, 1024, 4096, 8192, 16384)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        TestMemDb db = new();
        // Use a caching wrapper with simulated I/O latency, like the real TrieStore
        RawScopedTrieStore baseStore = new(db);
        _trieStore = new CachingSlowTrieStore(baseStore);

        // Build the tree using the base store (no delay during setup)
        PatriciaTree tree = new(baseStore, LimboLogs.Instance);

        Random rng = new(42);
        ValueHash256[] allKeys = new ValueHash256[TreeSize];

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(TreeSize);
        for (int i = 0; i < TreeSize; i++)
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

        _readKeys = new ValueHash256[KeyCount];
        Random readRng = new(123);
        for (int i = 0; i < KeyCount; i++)
        {
            _readKeys[i] = allKeys[readRng.Next(TreeSize)];
        }
    }

    [Benchmark(Baseline = true)]
    public void ReadOneByOne()
    {
        PatriciaTree tree = new(_trieStore, _rootHash, true, LimboLogs.Instance);
        for (int i = 0; i < _readKeys.Length; i++)
        {
            tree.Get(_readKeys[i].Bytes);
        }
    }

    [Benchmark]
    public void ParallelReadOneByOne()
    {
        PatriciaTree tree = new(_trieStore, _rootHash, true, LimboLogs.Instance);
        ValueHash256[] keys = _readKeys;
        Parallel.For(0, keys.Length, (i) =>
        {
            tree.Get(keys[i].Bytes);
        });
    }

    /// <summary>
    /// Wraps an IScopedTrieStore with a ConcurrentDictionary node cache (like the real TrieStore)
    /// and adds a spin-wait on LoadRlp to simulate disk I/O latency on cache misses.
    /// </summary>
    private sealed class CachingSlowTrieStore(IScopedTrieStore inner) : IScopedTrieStore
    {
        private const int SpinIterations = 200; // ~4µs spin per LoadRlp call
        private readonly ConcurrentDictionary<Hash256, TrieNode> _cache = new();

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash)
        {
            return _cache.GetOrAdd(hash, static h => new TrieNode(NodeType.Unknown, h));
        }

        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            Thread.SpinWait(SpinIterations);
            return inner.LoadRlp(in path, hash, flags);
        }

        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            Thread.SpinWait(SpinIterations);
            return inner.TryLoadRlp(in path, hash, flags);
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => inner.GetStorageTrieNodeResolver(address);
        public INodeStorage.KeyScheme Scheme => inner.Scheme;
        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);
    }
}

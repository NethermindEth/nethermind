// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.NodeLog;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Compares <see cref="PatriciaTree.BulkSet"/> followed by hashing and committing against <see cref="LogBulkSet"/>,
/// which hashes and writes to the trie node log as part of the bulk set. Every batch starts from the same preloaded trie.
/// </summary>
[MemoryDiagnoser]
[MinIterationTime(1000)]
public class LogBulkSetBenchmarks
{
    private const int EntryCount = 1024 * 10;
    private const int ColdGenerationBytes = 4 * 1024 * 1024;
    private const ulong WarmCompressedMaxMemory = 1UL << 30;
    private const int WarmedNodesPerEntry = 8;

    [Params(64, 512, 10240)]
    public int BatchSize { get; set; }

    [Params(0, 16384, 1048576)]
    public int PreloadedCount { get; set; }

    private PatriciaTree.BulkSetEntry[] _entries;
    private ParallelCommitTrieStore _trieStore;
    private Hash256 _preloadedRootHash;
    private TrieNodeLog _warmLog;
    private TrieRoot _warmRoot;
    private WarmedNodes _warmed;
    private DictionaryNodeSource _source;
    private WrittenNodes _written;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0);
        _entries = GenerateEntries(random, EntryCount);

        _trieStore = new ParallelCommitTrieStore(new RawScopedTrieStore(new MemDb()));
        _preloadedRootHash = Keccak.EmptyTreeHash;
        _warmLog = new TrieNodeLog(WarmCompressedMaxMemory, TrieNodeLog.DefaultGenerationBytes);
        _warmRoot = TrieRoot.Empty;
        _written = new WrittenNodes();
        Dictionary<TreePath, byte[]> preloadedNodes = [];
        _warmed = new WarmedNodes(BatchSize * WarmedNodesPerEntry);
        _source = new DictionaryNodeSource(preloadedNodes, _warmed, null);
        if (PreloadedCount > 0)
        {
            PatriciaTree.BulkSetEntry[] preload = GenerateEntries(random, PreloadedCount);

            PatriciaTree preloadTree = new(_trieStore, LimboLogs.Instance);
            using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(preload)) preloadTree.BulkSet(preloadSet);
            preloadTree.Commit();
            _preloadedRootHash = preloadTree.RootHash;

            using TrieNodeLogRetain preloadRetain = _warmLog.RetainActive();
            using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> logPreloadSet = new(preload))
            {
                _warmRoot = LogBulkSet.BulkSet(_warmLog, _source, null, TrieRoot.Empty, logPreloadSet, _written, canParallelize: true);
            }

            if (_warmRoot.Hash != _preloadedRootHash.ValueHash256) throw new InvalidOperationException($"Log root {_warmRoot.Hash} differs from {_preloadedRootHash}");

            foreach ((TreePath path, ulong pointer) in _written.AsSpan()) preloadedNodes[path] = _warmLog.GetRecord(pointer).Rlp.ToArray();
            _written.Clear();
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _written.Dispose();
        _warmLog.Dispose();
    }

    /// <summary>The production flow before the trie node log: hash with the write batch, persist at the end of the block.</summary>
    [Benchmark(Baseline = true)]
    public void BulkSetUpdateRootHashAndCommit()
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batch = new(BatchSize);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            batch.Clear();
            batch.AddRange(_entries.AsSpan(start, BatchSize));
            PatriciaTree tree = new(_trieStore, _preloadedRootHash, true, LimboLogs.Instance);
            tree.BulkSet(batch);
            tree.UpdateRootHash(canBeParallel: true);
            tree.Commit();
        }
    }

    /// <summary>Every batch reads the preloaded trie from the node source and writes to a log created for the invocation.</summary>
    [Benchmark]
    public void LogBulkSetColdLog()
    {
        using TrieNodeLog log = new(0, ColdGenerationBytes);
        DoLogBulkSet(log, new TrieRoot(_preloadedRootHash.ValueHash256, 0));
    }

    /// <summary>Like <see cref="LogBulkSetColdLog"/>, but every batch finds the preloaded trie in a shared log the preload filled.</summary>
    [Benchmark]
    public void LogBulkSetWarmLog() => DoLogBulkSet(_warmLog, _warmRoot);

    private void DoLogBulkSet(TrieNodeLog log, TrieRoot root)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batch = new(BatchSize);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            batch.Clear();
            batch.AddRange(_entries.AsSpan(start, BatchSize));
            LogBulkSet.BulkSet(log, _source, null, root, batch, _written, canParallelize: true);
            // Every batch stands for a block, which fetches the nodes it reads again.
            _written.Clear();
            _warmed.Reset();
        }
    }

    private static PatriciaTree.BulkSetEntry[] GenerateEntries(Random random, int count)
    {
        PatriciaTree.BulkSetEntry[] entries = new PatriciaTree.BulkSetEntry[count];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[32];
            random.NextBytes(key);
            byte[] value = new byte[32];
            random.NextBytes(value);
            entries[i] = new PatriciaTree.BulkSetEntry(new ValueHash256(key), value);
        }

        return entries;
    }

    /// <summary>
    /// Lets <see cref="PatriciaTree.Commit"/> split across threads like the production committers do.
    /// </summary>
    /// <remarks>The <see cref="MemDb"/> batch writes straight to a concurrent dictionary, so concurrent commits are safe.</remarks>
    private sealed class ParallelCommitTrieStore(IScopedTrieStore inner) : IScopedTrieStore
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(in path, hash);

        public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.LoadRlp(in path, hash, flags);

        public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => inner.TryLoadRlp(in path, hash, flags);

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => inner.GetStorageTrieNodeResolver(address);

        public INodeStorage.KeyScheme Scheme => inner.Scheme;

        public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => new Committer(inner.BeginCommit(root, writeFlags));

        private sealed class Committer(ICommitter inner) : ICommitter
        {
            public TrieNode CommitNode(ref TreePath path, TrieNode node) => inner.CommitNode(ref path, node);

            public bool TryEnableParallelCommit() => true;

            public void Dispose() => inner.Dispose();
        }
    }
}

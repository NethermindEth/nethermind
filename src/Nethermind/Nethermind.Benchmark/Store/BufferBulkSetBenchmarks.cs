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
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Compares <see cref="PatriciaTree.BulkSet"/> followed by hashing and committing against <see cref="BufferBulkSet"/>,
/// which hashes and writes to the trie node buffer as part of the bulk set. Every batch starts from the same preloaded trie.
/// </summary>
[MemoryDiagnoser]
[MinIterationTime(1000)]
public class BufferBulkSetBenchmarks
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
    private PatriciaTree.BulkSetEntry[] _preload;
    private ParallelCommitTrieStore _trieStore;
    private Hash256 _preloadedRootHash;
    private NodeRecordBuffer _coldBuffer;
    private NodeRecordBuffer _warmBuffer;
    private NodeRecordBufferRetain _warmBufferRetain;
    private TrieRoot _warmRoot;
    private int _fetchCountBeforeWarmBuffer;
    private WarmedNodes _warmedNodes;
    private DictionaryNodeSource _nodeSource;

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(0);
        _entries = GenerateEntries(random, EntryCount);

        _trieStore = new ParallelCommitTrieStore(new RawScopedTrieStore(new MemDb()));
        _preloadedRootHash = Keccak.EmptyTreeHash;
        _preload = GenerateEntries(random, PreloadedCount);
        Dictionary<TreePath, byte[]> preloadedNodes = [];
        _warmedNodes = new WarmedNodes(BatchSize * WarmedNodesPerEntry);
        _nodeSource = new DictionaryNodeSource(preloadedNodes, _warmedNodes, null);
        if (PreloadedCount > 0)
        {
            PatriciaTree preloadTree = new(_trieStore, LimboLogs.Instance);
            using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(_preload)) preloadTree.BulkSet(preloadSet);
            preloadTree.Commit();
            _preloadedRootHash = preloadTree.RootHash;

            using NodeRecordBuffer preloadBuffer = new(0, NodeRecordBuffer.DefaultGenerationBytes);
            using NodeRecordBufferRetain preloadRetain = preloadBuffer.RetainActive();
            TrieRoot root = PreloadBuffer(preloadBuffer);
            foreach ((TreePath path, _, NodeRecord record) in preloadBuffer.EnumerateNodes(root.Pointer)) preloadedNodes[path] = record.Rlp.ToArray();
        }
    }

    /// <summary>The production flow before the trie node buffer: hash with the write batch, persist at the end of the block.</summary>
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

    [IterationSetup(Target = nameof(BufferBulkSetColdBuffer))]
    public void CreateColdBuffer() => _coldBuffer = new NodeRecordBuffer(0, ColdGenerationBytes);

    [IterationCleanup(Target = nameof(BufferBulkSetColdBuffer))]
    public void ReleaseColdBuffer() => _coldBuffer.Dispose();

    /// <summary>Every batch reads the preloaded trie from the node source and writes to a buffer created for the iteration.</summary>
    [Benchmark]
    public void BufferBulkSetColdBuffer() => DoBufferBulkSet(_coldBuffer, new TrieRoot(_preloadedRootHash.ValueHash256, 0));

    [IterationSetup(Target = nameof(BufferBulkSetWarmBuffer))]
    public void CreateWarmBuffer()
    {
        _warmBuffer = new NodeRecordBuffer(WarmCompressedMaxMemory, NodeRecordBuffer.DefaultGenerationBytes);
        _warmBufferRetain = _warmBuffer.RetainActive();
        _warmRoot = PreloadBuffer(_warmBuffer);
        _fetchCountBeforeWarmBuffer = _nodeSource.FetchCount;
    }

    [IterationCleanup(Target = nameof(BufferBulkSetWarmBuffer))]
    public void ReleaseWarmBuffer()
    {
        _warmBuffer.Dispose();
        _warmBufferRetain.Dispose();
        if (_nodeSource.FetchCount != _fetchCountBeforeWarmBuffer) throw new InvalidOperationException("The warm buffer case fetched nodes from the node source");
    }

    /// <summary>
    /// Like <see cref="BufferBulkSetColdBuffer"/>, but every batch reads the preloaded trie in place from a buffer the iteration's
    /// preload filled and retains, as a scope's retain keeps the nodes of in-memory blocks readable.
    /// </summary>
    [Benchmark]
    public void BufferBulkSetWarmBuffer() => DoBufferBulkSet(_warmBuffer, _warmRoot);

    private TrieRoot PreloadBuffer(NodeRecordBuffer buffer)
    {
        if (_preload.Length == 0) return TrieRoot.Empty;

        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(_preload);
        TrieRoot root = BufferBulkSet.BulkSet(buffer, _nodeSource, null, TrieRoot.Empty, preloadSet, canParallelize: true);
        if (root.Hash != _preloadedRootHash.ValueHash256) throw new InvalidOperationException($"Buffer root {root.Hash} differs from {_preloadedRootHash}");
        return root;
    }

    private void DoBufferBulkSet(NodeRecordBuffer buffer, TrieRoot root)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batch = new(BatchSize);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            batch.Clear();
            batch.AddRange(_entries.AsSpan(start, BatchSize));
            BufferBulkSet.BulkSet(buffer, _nodeSource, null, root, batch, canParallelize: true);
            // Every batch stands for a block, which fetches the nodes it reads again.
            _warmedNodes.Reset();
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

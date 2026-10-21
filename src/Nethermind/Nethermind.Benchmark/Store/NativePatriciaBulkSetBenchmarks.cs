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
using Nethermind.Trie.Native;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Compares <see cref="PatriciaTree.BulkSet"/> followed by hashing (and committing) against <see cref="NativePatriciaBulkSet"/>,
/// which hashes and writes as part of the bulk set. Every batch starts from the same preloaded trie.
/// </summary>
[MemoryDiagnoser]
[MinIterationTime(1000)]
public class NativePatriciaBulkSetBenchmarks
{
    private const int EntryCount = 1024 * 10;

    [Params(64, 512, 10240)]
    public int BatchSize { get; set; }

    [Params(0, 16384, 1048576)]
    public int PreloadedCount { get; set; }

    private PatriciaTree.BulkSetEntry[] _entries;
    private NativeBulkSetEntry[] _nativeEntries;
    private NativeBulkSetEntry[] _nativeBatches;
    private byte[] _values;
    private ParallelCommitTrieStore _trieStore;
    private Hash256 _preloadedRootHash;
    private DictionaryNodeStore _nativeStore;
    private NativePendingNodes _nativePending;
    private NativeNodeBuffer _warmBuffer;

    [GlobalSetup]
    public void Setup()
    {
        Random rng = new(0);
        (_entries, _nativeEntries, _values) = GenerateEntries(rng, EntryCount);
        _nativeBatches = new NativeBulkSetEntry[EntryCount];

        _trieStore = new ParallelCommitTrieStore(new RawScopedTrieStore(new MemDb()));
        _preloadedRootHash = Keccak.EmptyTreeHash;
        _warmBuffer = new NativeNodeBuffer(1UL << 30, NativeNodeBuffer.DefaultLiveGenerations);
        Dictionary<TreePath, byte[]> preloadedNodes = [];
        if (PreloadedCount > 0)
        {
            (PatriciaTree.BulkSetEntry[] preload, NativeBulkSetEntry[] nativePreload, byte[] preloadValues) = GenerateEntries(rng, PreloadedCount);

            PatriciaTree preloadTree = new(_trieStore, LimboLogs.Instance);
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(preload);
            preloadTree.BulkSet(preloadSet);
            preloadTree.Commit();
            _preloadedRootHash = preloadTree.RootHash;

            using NativePendingNodes preloadPending = NativePendingNodes.Rent(PreloadedCount);
            Hash256 nativeRoot = NativePatriciaBulkSet.BulkSet(new DictionaryNodeStore(preloadedNodes), preloadPending, _warmBuffer, null, null, Keccak.EmptyTreeHash.ValueHash256, nativePreload, preloadValues);
            if (nativeRoot != _preloadedRootHash) throw new InvalidOperationException($"Native root {nativeRoot} differs from {_preloadedRootHash}");

            for (int shard = 0; shard < preloadPending.ShardCount; shard++)
            {
                NativePendingNodesView view = preloadPending.GetView(shard);
                for (int i = 0; i < view.Count; i++) preloadedNodes[view[i].Path] = view.GetRlp(view[i]).ToArray();
            }
        }

        _nativeStore = new DictionaryNodeStore(preloadedNodes);
        _nativePending = NativePendingNodes.Rent(BatchSize);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _nativePending.Dispose();
        _warmBuffer.Dispose();
    }

    /// <summary>The production flow: hash with the write batch, persist at the end of the block.</summary>
    [Benchmark(Baseline = true)]
    public void BulkSetUpdateRootHashAndCommit() => DoBulkSet(static tree =>
    {
        tree.UpdateRootHash(canBeParallel: true);
        tree.Commit();
    });

    [Benchmark]
    public void BulkSetAndCommit() => DoBulkSet(static tree => tree.Commit());

    [Benchmark]
    public void BulkSetAndUpdateRootHash() => DoBulkSet(static tree => tree.UpdateRootHash(canBeParallel: true));

    [Benchmark]
    public void NativeBulkSet()
    {
        _nativeEntries.CopyTo(_nativeBatches, 0);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            NativePatriciaBulkSet.BulkSet(_nativeStore, _nativePending, null, null, null, _preloadedRootHash.ValueHash256, _nativeBatches.AsSpan(start, BatchSize), _values);
            // Cleared after every batch, so every batch starts from the preloaded trie.
            _nativePending.Clear();
        }
    }

    /// <summary>Like <see cref="NativeBulkSet"/>, but every batch finds the preloaded trie in a shared buffer the preload filled.</summary>
    [Benchmark]
    public void NativeBulkSetWarmBuffer()
    {
        _nativeEntries.CopyTo(_nativeBatches, 0);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            NativePatriciaBulkSet.BulkSet(_nativeStore, _nativePending, _warmBuffer, null, null, _preloadedRootHash.ValueHash256, _nativeBatches.AsSpan(start, BatchSize), _values);
            _nativePending.Clear();
        }
    }

    private void DoBulkSet(Action<PatriciaTree> finish)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> batch = new(BatchSize);
        for (int start = 0; start < EntryCount; start += BatchSize)
        {
            batch.Clear();
            batch.AddRange(_entries.AsSpan(start, BatchSize));
            PatriciaTree tree = new(_trieStore, _preloadedRootHash, true, LimboLogs.Instance);
            tree.BulkSet(batch);
            finish(tree);
        }
    }

    private static (PatriciaTree.BulkSetEntry[], NativeBulkSetEntry[], byte[]) GenerateEntries(Random rng, int count)
    {
        PatriciaTree.BulkSetEntry[] entries = new PatriciaTree.BulkSetEntry[count];
        NativeBulkSetEntry[] nativeEntries = new NativeBulkSetEntry[count];
        byte[] values = new byte[count * 32];
        for (int i = 0; i < count; i++)
        {
            byte[] key = new byte[32];
            rng.NextBytes(key);
            Span<byte> value = values.AsSpan(i * 32, 32);
            rng.NextBytes(value);
            entries[i] = new PatriciaTree.BulkSetEntry(new ValueHash256(key), value.ToArray());
            nativeEntries[i] = new NativeBulkSetEntry(new ValueHash256(key), i * 32, 32);
        }

        return (entries, nativeEntries, values);
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

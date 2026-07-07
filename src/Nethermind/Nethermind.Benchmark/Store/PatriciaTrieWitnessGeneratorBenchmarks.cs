// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store
{
    /// <summary>
    /// Compares the new mutation-free <see cref="PatriciaTrieWitnessGenerator"/> (sequential and parallel) against
    /// the old "capture trie reads during the actual mutation" technique it replaces.
    /// </summary>
    [MemoryDiagnoser]
    [MinIterationTime(1000)]
    public class PatriciaTrieWitnessGeneratorBenchmarks
    {
        [Params(100_000)]
        public int TrieSize { get; set; }

        [Params(1_000, 5_000)]
        public int TouchedCount { get; set; }

        [Params(0.0, 0.5)]
        public double DeleteFraction { get; set; }

        private MemDb _db;
        private Hash256 _root;
        private PatriciaTrieWitnessGenerator.PathEntry[] _entries;
        private Hash256[] _reads;
        private Hash256[] _deletes;

        [GlobalSetup]
        public void Setup()
        {
            Random rng = new(0);

            MemDb db = new();
            RawScopedTrieStore store = new(db);
            PatriciaTree tree = new(store, LimboLogs.Instance);

            Hash256[] keys = new Hash256[TrieSize];
            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulk = new(TrieSize);
            for (int i = 0; i < TrieSize; i++)
            {
                byte[] keyBuf = new byte[32];
                rng.NextBytes(keyBuf);
                byte[] valueBuf = new byte[32];
                rng.NextBytes(valueBuf);
                keys[i] = new Hash256(keyBuf);
                bulk.Add(new PatriciaTree.BulkSetEntry(keys[i], valueBuf));
            }
            tree.BulkSet(bulk);
            tree.Commit();

            _db = db;
            _root = tree.RootHash;

            int deleteCount = (int)(TouchedCount * DeleteFraction);
            _entries = new PatriciaTrieWitnessGenerator.PathEntry[TouchedCount];
            List<Hash256> reads = [];
            List<Hash256> deletes = [];
            for (int i = 0; i < TouchedCount; i++)
            {
                Hash256 key = keys[rng.Next(TrieSize)];
                bool isDeleted = i < deleteCount;
                _entries[i] = new PatriciaTrieWitnessGenerator.PathEntry(
                    key,
                    isDeleted ? PatriciaTrieWitnessGenerator.AccessType.Delete : PatriciaTrieWitnessGenerator.AccessType.Read);
                (isDeleted ? deletes : reads).Add(key);
            }

            _reads = [.. reads];
            _deletes = [.. deletes];
        }

        [Benchmark(Baseline = true)]
        public int Old_CaptureDuringMutation()
        {
            CapturingScopedTrieStore store = new(new RawScopedTrieStore(_db));
            PatriciaTree tree = new(store, LimboLogs.Instance) { RootHash = _root };
            foreach (Hash256 key in _reads) tree.Get(key.Bytes);
            foreach (Hash256 key in _deletes) tree.Set(key.Bytes, (byte[])null);
            tree.UpdateRootHash();
            return store.Captured.Count;
        }

        [Benchmark]
        public int New_Sequential()
        {
            CountingSink sink = new();
            PatriciaTrieWitnessGenerator.Generate(new RawScopedTrieStore(_db), _root, _entries, sink, parallelize: false);
            return sink.Count;
        }

        [Benchmark]
        public int New_Parallel()
        {
            CountingSink sink = new();
            PatriciaTrieWitnessGenerator.Generate(new RawScopedTrieStore(_db), _root, _entries, sink, parallelize: true);
            return sink.Count;
        }

        private sealed class CountingSink : PatriciaTrieWitnessGenerator.ISink
        {
            private int _count;
            public int Count => _count;
            public void Add(in TreePath path, TrieNode node) => Interlocked.Increment(ref _count);
        }

        private sealed class CapturingScopedTrieStore(IScopedTrieStore baseStore) : IScopedTrieStore
        {
            public Dictionary<Hash256AsKey, byte[]> Captured { get; } = [];

            public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => baseStore.FindCachedOrUnknown(in path, hash);

            public byte[] LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => Capture(hash, baseStore.LoadRlp(in path, hash, flags));

            public byte[] TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => Capture(hash, baseStore.TryLoadRlp(in path, hash, flags));

            private byte[] Capture(Hash256 hash, byte[] rlp)
            {
                if (rlp is not null) Captured[hash] = rlp;
                return rlp;
            }

            public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256 address) => baseStore.GetStorageTrieNodeResolver(address);

            public INodeStorage.KeyScheme Scheme => baseStore.Scheme;

            public ICommitter BeginCommit(TrieNode root, WriteFlags writeFlags = WriteFlags.None) => baseStore.BeginCommit(root, writeFlags);
        }
    }
}

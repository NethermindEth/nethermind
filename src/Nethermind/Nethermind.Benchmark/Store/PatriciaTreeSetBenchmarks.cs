// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store
{
    [MemoryDiagnoser]
    [MinIterationTime(1000)]
    public class PatriciaTreeSetBenchmarks
    {
        private const int _entryCount = 1024 * 10;

        [Params(2, 4, 8, 64, 512, 10240)]
        public int BatchSize { get; set; }

        [Params(false, true)]
        public bool PreSorted { get; set; }

        [Params(0, 16384)]
        public int PreloadedCount { get; set; }

        private (Hash256, Account)[] _entries;
        private MemDb _backingMemDb;
        private Hash256 _preloadedRootHash;

        [GlobalSetup]
        public void Setup()
        {
            Random rand = new(0);

            _entries = new (Hash256, Account)[_entryCount];
            for (int i = 0; i < _entryCount; i++)
            {
                Hash256 address = Keccak.Compute(i.ToBigEndianByteArray());
                _entries[i] = (address, new Account((UInt256)rand.NextInt64()));
            }

            if (PreSorted)
            {
                for (int i = 0; i < _entryCount; i += BatchSize)
                {
                    Array.Sort(_entries, i, BatchSize, Comparer<(Hash256, Account)>.Create(static (a, b) => a.Item1.CompareTo(b.Item1)));
                }
            }

            _backingMemDb = new MemDb();
            _preloadedRootHash = Keccak.EmptyTreeHash;

            if (PreloadedCount > 0)
            {
                TrieStore preloadStore = TestTrieStoreFactory.Build(_backingMemDb,
                    Prune.WhenCacheReaches(1.MiB),
                    Persist.EveryNBlock(2), NullLogManager.Instance);
                StateTree preloadTree = new(preloadStore, NullLogManager.Instance);
                preloadTree.RootHash = Keccak.EmptyTreeHash;

                using ArrayPoolListRef<PatriciaTree.BulkSetEntry> preloadSet = new(PreloadedCount);
                for (int i = 0; i < PreloadedCount; i++)
                {
                    Hash256 address = Keccak.Compute((i + _entryCount).ToBigEndianByteArray());
                    Account account = new((UInt256)rand.NextInt64());
                    Serialization.Rlp.Rlp rlp = account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                    preloadSet.Add(new PatriciaTree.BulkSetEntry(address, rlp?.Bytes));
                }
                preloadTree.BulkSet(preloadSet);

                using IBlockCommitter _ = preloadStore.BeginBlockCommit(0);
                preloadTree.Commit();
                _preloadedRootHash = preloadTree.RootHash;
            }
        }

        [Benchmark]
        public void RepeatedSet()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(_backingMemDb,
                Prune.WhenCacheReaches(1.MiB),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new(trieStore, NullLogManager.Instance);
            tempTree.RootHash = _preloadedRootHash;

            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    tempTree.RootHash = _preloadedRootHash;
                }

                (Hash256 address, Account value) = _entries[i];
                tempTree.Set(address, value);
            }
        }

        [Benchmark]
        public void RepeatedBulkSet() => DoBulkSet(PatriciaTree.Flags.None);

        [Benchmark]
        public void RepeatedBulkSetNoParallel() => DoBulkSet(PatriciaTree.Flags.DoNotParallelize);

        private void DoBulkSet(PatriciaTree.Flags flags)
        {
            if (PreSorted) flags |= PatriciaTree.Flags.WasSorted;

            TrieStore trieStore = TestTrieStoreFactory.Build(_backingMemDb,
                Prune.WhenCacheReaches(1.MiB),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new(trieStore, NullLogManager.Instance);
            tempTree.RootHash = _preloadedRootHash;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkSet = new(BatchSize);
            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    tempTree.BulkSet(bulkSet, flags);
                    bulkSet.Clear();
                    tempTree.RootHash = _preloadedRootHash;
                }

                (Hash256 address, Account account) = _entries[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTree.BulkSetEntry(address, rlp?.Bytes));
            }

            tempTree.BulkSet(bulkSet, flags);
        }
    }
}

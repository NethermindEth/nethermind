// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        private (Hash256, Account)[] _entries;

        [GlobalSetup]
        public void Setup()
        {
            _entries = new (Hash256, Account)[_entryCount];
            Random rand = new(0);
            for (int i = 0; i < _entryCount; i++)
            {
                Hash256 address = Keccak.Compute(i.ToBigEndianByteArray());
                _entries[i] = (address, new Account((UInt256)rand.NextInt64()));
            }

            if (PreSorted)
            {
                Array.Sort(_entries, static (a, b) => a.Item1.CompareTo(b.Item1));
            }
        }

        [Benchmark]
        public void RepeatedSet()
        {
            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new(trieStore, NullLogManager.Instance);
            Hash256 originalRootHash = Keccak.EmptyTreeHash;
            tempTree.RootHash = Keccak.EmptyTreeHash;

            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    tempTree.RootHash = originalRootHash;
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

            TrieStore trieStore = TestTrieStoreFactory.Build(new MemDb(),
                Prune.WhenCacheReaches(1.MiB),
                Persist.EveryNBlock(2), NullLogManager.Instance);
            StateTree tempTree = new(trieStore, NullLogManager.Instance);
            Hash256 originalRootHash = Keccak.EmptyTreeHash;
            tempTree.RootHash = Keccak.EmptyTreeHash;

            using ArrayPoolListRef<PatriciaTree.BulkSetEntry> bulkSet = new(BatchSize);
            for (int i = 0; i < _entryCount; i++)
            {
                if (i % BatchSize == 0)
                {
                    tempTree.BulkSet(bulkSet, flags);
                    bulkSet.Clear();
                    tempTree.RootHash = originalRootHash;
                }

                (Hash256 address, Account account) = _entries[i];
                Serialization.Rlp.Rlp rlp = account is null ? null : account.IsTotallyEmpty ? StateTree.EmptyAccountRlp : Serialization.Rlp.Rlp.Encode(account);
                bulkSet.Add(new PatriciaTree.BulkSetEntry(address, rlp?.Bytes));
            }

            tempTree.BulkSet(bulkSet, flags);
        }
    }
}

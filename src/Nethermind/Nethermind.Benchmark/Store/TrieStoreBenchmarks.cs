// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store
{
    [MemoryDiagnoser]
    public class TrieStoreBenchmarks
    {
        private TrieStore.DirtyNodesCache _cache;
        private static readonly Keccak _key = Keccak.Compute("");

        [GlobalSetup]
        public void Setup()
        {
            _cache = new(new TrieStore(new MemDb("test"), NullLogManager.Instance));

            // fill it up
            for (int i = 0; i < 10000; i++)
            {
                _cache.FindCachedOrUnknown(Keccak.Compute(i.ToString()));
            }
        }

        [Benchmark]
        public void DirtyNodesCache_FindCachedOrUnknown_And_Delete()
        {
            _cache.FindCachedOrUnknown(_key);
            _cache.Remove(_key);
        }
    }
}

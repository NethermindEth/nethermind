// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State
{
    [MemoryDiagnoser]
    public class StorageTreeHashCacheBenchmark
    {
        private const int CellIndexMemoizationCount = 16;
        private static readonly int _count = 1024 * 2;
        private static readonly byte[] _value = { 1 };
        private StorageTree _storage;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _storage = new StorageTree(new TrieStore(new MemDb(), LimboLogs.Instance), LimboLogs.Instance);

            for (int i = 0; i < _count; i++)
            {
                _storage.Set((UInt256)i, i.ToByteArray());
            }
        }

        [Benchmark(OperationsPerInvoke = 1024)]
        public void CellIndexes_cached()
        {
            for (int i = 0; i < 1024; i++)
            {
                _storage.Get((UInt256)i);
            }
        }

        [Benchmark]
        public void CellIndexes_memoized_for_writes()
        {
            const int size = CellIndexMemoizationCount;
            const int cachedOffset = 10000;

            for (int i = 0; i < size; i++)
            {
                _storage.Get((UInt256)i + cachedOffset);
            }

            for (int i = 0; i < size; i++)
            {
                _storage.Set((UInt256)i + cachedOffset, _value);
            }
        }

        [Benchmark]
        public void CellIndexes_beyond_memoization_for_writes()
        {
            const int size = CellIndexMemoizationCount * 2;
            const int cachedOffset = 10000;

            for (int i = 0; i < size; i++)
            {
                _storage.Get((UInt256)i + cachedOffset);
            }

            for (int i = 0; i < size; i++)
            {
                _storage.Set((UInt256)i + cachedOffset, _value);
            }
        }
    }
}

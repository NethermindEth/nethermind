// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.State
{
    [MemoryDiagnoser]
    public class StorageTreeBenchmark
    {
        private static readonly UInt256 _index = new(0, 1, 2, 3);
        private static readonly byte[] _value = { 17, 19, 23 };

        private StorageTree _tree;

        [GlobalSetup]
        public void Setup()
        {
            _tree = new StorageTree(NullTrieStore.Instance, NullLogManager.Instance);
        }

        [Benchmark]
        public void Set_index()
        {
            _tree.Set(_index, _value);
        }

        [Benchmark]
        public byte[] Get_index()
        {
            return _tree.Get(_index);
        }
    }
}

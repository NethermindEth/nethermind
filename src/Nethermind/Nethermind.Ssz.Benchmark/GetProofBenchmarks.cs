// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core2.Types;
using Nethermind.Merkleization;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class GetProofBenchmarks
    {
        private Bytes32[] _bytes = new Bytes32[1024];

        [GlobalSetup]
        public void GlobalSetup()
        {
            for (int i = 0; i < _bytes.Length; i++)
            {
                byte[] bytes = new byte[32];
                bytes[i % 32] = (byte)i;
                _bytes[i] = Bytes32.Wrap(bytes);
            }
        }

        [Params(1, 32, 128, 1024)]
        public int ItemsCount { get; set; }

        [Benchmark(Baseline = true)]
        public void Current()
        {
            for (int i = 0; i < ItemsCount; i++)
            {
                var tree = OldMerkleHelper.CalculateMerkleTreeFromLeaves(_bytes[..ItemsCount]);
                OldMerkleHelper.GetMerkleProof(tree, i, 32);
            }
        }

        [Benchmark]
        public void New()
        {
            ShaMerkleTree shaMerkleTree = new ShaMerkleTree(new MemMerkleTreeStore());
            for (int i = 0; i < ItemsCount; i++)
            {
                shaMerkleTree.Insert(_bytes[i]);
                shaMerkleTree.GetProof((uint)i);
            }
        }
    }
}

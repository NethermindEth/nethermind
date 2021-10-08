//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
                bytes[i % 32] = (byte) i;
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
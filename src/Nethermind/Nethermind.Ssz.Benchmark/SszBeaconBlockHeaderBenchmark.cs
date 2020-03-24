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
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class SszBeaconBlockHeaderBenchmark
    {
        public static BlsPublicKey TestKey1 = new BlsPublicKey(
            "0x000102030405060708090a0b0c0d0e0f" +
            "101112131415161718191a1b1c1d1e1f" +
            "202122232425262728292a2b2c2d2e2f");

        public static BlsSignature TestSig1 = new BlsSignature(new byte[BlsSignature.Length]);
        
        private BeaconBlockHeader _header = BeaconBlockHeader.Zero;
        private byte[] _encoded = new byte[Ssz.BeaconBlockHeaderLength];
        
        public SszBeaconBlockHeaderBenchmark()
        {
            new BeaconBlockHeader(
                new Slot(1),
                new Root(Sha256.OfAnEmptySequenceRlp.AsSpan()),
                new Root(Sha256.OfAnEmptySequenceRlp.AsSpan()),
                new Root(Sha256.OfAnEmptySequenceRlp.AsSpan()));
        }
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Ssz.Encode(_encoded, _header);
        }
    }
}
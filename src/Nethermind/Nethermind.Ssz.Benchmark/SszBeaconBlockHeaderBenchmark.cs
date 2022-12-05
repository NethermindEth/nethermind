// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

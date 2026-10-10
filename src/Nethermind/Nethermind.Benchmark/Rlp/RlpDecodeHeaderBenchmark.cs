// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Rlp
{
    /// <inheritdoc cref="RlpDecodeReceiptBenchmark"/>
    public class RlpDecodeHeaderBenchmark
    {
        private const int Batch = 256;

        private byte[] _header;

        private readonly byte[][] _scenarios =
        [
            Serialization.Rlp.Rlp.Encode(Build.A.BlockHeader.TestObject).Bytes,
            Serialization.Rlp.Rlp.Encode(
                Build.A.BlockHeader.WithBaseFee(42).WithWithdrawalsRoot(TestItem.KeccakA)
                    .WithBlobGasUsed(1024).WithExcessBlobGas(2048)
                    .WithParentBeaconBlockRoot(TestItem.KeccakB).TestObject).Bytes,
        ];

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup() => _header = _scenarios[ScenarioIndex];

        [Benchmark(OperationsPerInvoke = Batch)]
        public BlockHeader Current()
        {
            BlockHeader header = null;
            for (int i = 0; i < Batch; i++)
            {
                header = Serialization.Rlp.Rlp.Decode<BlockHeader>(_header);
            }

            return header;
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    /// <inheritdoc cref="RlpDecodeReceiptBenchmark"/>
    public class RlpDecodeTxBenchmark
    {
        private const int Batch = 256;

        private byte[] _tx;

        private readonly byte[][] _scenarios;

        public RlpDecodeTxBenchmark()
        {
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            _scenarios =
            [
                Serialization.Rlp.Rlp.Encode(
                    Build.A.Transaction.Signed(ecdsa, TestItem.PrivateKeyA).TestObject,
                    RlpBehaviors.SkipTypedWrapping).Bytes,
                Serialization.Rlp.Rlp.Encode(
                    Build.A.Transaction.WithType(TxType.EIP1559).WithMaxFeePerGas(30)
                        .Signed(ecdsa, TestItem.PrivateKeyA).TestObject,
                    RlpBehaviors.SkipTypedWrapping).Bytes,
                Serialization.Rlp.Rlp.Encode(
                    Build.A.Transaction.WithType(TxType.AccessList)
                        .WithAccessList(Build.An.AccessList.TestObject)
                        .Signed(ecdsa, TestItem.PrivateKeyA).TestObject,
                    RlpBehaviors.SkipTypedWrapping).Bytes,
            ];
        }

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup() => _tx = _scenarios[ScenarioIndex];

        [Benchmark(OperationsPerInvoke = Batch)]
        public Transaction Current()
        {
            Transaction transaction = null;
            for (int i = 0; i < Batch; i++)
            {
                transaction = Serialization.Rlp.Rlp.Decode<Transaction>(_tx, RlpBehaviors.SkipTypedWrapping);
            }

            return transaction;
        }
    }
}

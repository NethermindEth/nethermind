// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    /// <remarks>
    /// One decode is far below this machine's scheduling jitter, so each benchmark repeats
    /// <see cref="Batch"/> decodes per invocation and reports the per-decode figure.
    /// <see cref="Control"/> touches no RLP code: when it moves between runs, the run drifted.
    /// </remarks>
    public class RlpDecodeReceiptBenchmark
    {
        private const int Batch = 256;

        private static readonly ReceiptMessageDecoder MessageDecoder = new();
        private static readonly ReceiptStorageDecoder StorageDecoder = new();

        private byte[] _message;
        private byte[] _storage;

        private readonly TxReceipt[] _scenarios =
        [
            Build.A.Receipt.WithAllFieldsFilled.WithLogs([]).TestObject,
            Build.A.Receipt.WithAllFieldsFilled.TestObject,
            Build.A.Receipt.WithAllFieldsFilled.WithLogs(
                Build.A.LogEntry.TestObject,
                Build.A.LogEntry.TestObject,
                Build.A.LogEntry.TestObject,
                Build.A.LogEntry.TestObject).TestObject,
        ];

        [Params(0, 1, 2)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            TxReceipt receipt = _scenarios[ScenarioIndex];
            _message = MessageDecoder.EncodeAsBytes(receipt);
            _storage = StorageDecoder.EncodeAsBytes(receipt, RlpBehaviors.Storage);
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public TxReceipt Message()
        {
            TxReceipt receipt = null;
            for (int i = 0; i < Batch; i++)
            {
                RlpReader reader = new(_message);
                receipt = MessageDecoder.Decode(ref reader);
            }

            return receipt;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public TxReceipt Storage()
        {
            TxReceipt receipt = null;
            for (int i = 0; i < Batch; i++)
            {
                RlpReader reader = new(_storage);
                receipt = StorageDecoder.Decode(ref reader, RlpBehaviors.Storage);
            }

            return receipt;
        }

        [Benchmark(OperationsPerInvoke = Batch)]
        public Hash256 Control()
        {
            Hash256 hash = null;
            for (int i = 0; i < Batch; i++)
            {
                hash = Keccak.Compute(_message);
            }

            return hash;
        }
    }
}

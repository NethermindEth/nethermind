// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Rlp
{
    public class RlpDecodeReceiptBenchmark
    {
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

        [Benchmark]
        public TxReceipt Message()
        {
            RlpReader reader = new(_message);
            return MessageDecoder.Decode(ref reader);
        }

        [Benchmark]
        public TxReceipt Storage()
        {
            RlpReader reader = new(_storage);
            return StorageDecoder.Decode(ref reader, RlpBehaviors.Storage);
        }
    }
}

// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Benchmarks.Receipts
{
    [MemoryDiagnoser]
    public class ReceiptLogFilterBenchmark
    {
        private readonly ReceiptStorageDecoder _decoder = new();
        private byte[] _receiptRlp = null!;
        private AddressFilter _addressFilter = null!;
        private SequenceTopicsFilter _topicsFilter = null!;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _receiptRlp = _decoder.Encode(BuildReceipt(), RlpBehaviors.Storage).Bytes;
            _addressFilter = new AddressFilter(TestItem.AddressA);
            _topicsFilter = new SequenceTopicsFilter(new SpecificTopic(TestItem.KeccakA));
        }

        [Benchmark]
        public int DecodeAndFilter()
        {
            RlpReader reader = new(_receiptRlp);
            _decoder.DecodeStructRef(ref reader, RlpBehaviors.Storage, out TxReceiptStructRef receipt);

            int matched = 0;
            LogEntriesIterator logs = new(receipt.LogsRlp, _decoder);
            while (logs.TryGetNext(out LogEntryStructRef log))
            {
                if (_addressFilter.Accepts(ref log.Address) && _topicsFilter.Accepts(ref log))
                {
                    matched++;
                }
            }

            return matched;
        }

        private static TxReceipt BuildReceipt()
        {
            byte[] data32 = new byte[32];
            byte[] data64 = new byte[64];
            byte[] data128 = new byte[128];

            LogEntry[] logs =
            [
                new LogEntry(TestItem.AddressA, data32, [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC]),
                new LogEntry(TestItem.AddressB, data32, [TestItem.KeccakA, TestItem.KeccakE]),
                new LogEntry(TestItem.AddressA, data64, [TestItem.KeccakD, TestItem.KeccakF]),
                new LogEntry(TestItem.AddressA, data32, [TestItem.KeccakA, TestItem.KeccakG, TestItem.KeccakH]),
                new LogEntry(TestItem.AddressC, data128, [TestItem.KeccakD]),
                new LogEntry(TestItem.AddressA, data32, [TestItem.KeccakA]),
            ];

            return new TxReceipt
            {
                TxType = TxType.EIP1559,
                StatusCode = 1,
                BlockNumber = 21_000_000,
                BlockHash = TestItem.KeccakH,
                Index = 7,
                Sender = TestItem.AddressD,
                Recipient = TestItem.AddressA,
                ContractAddress = TestItem.AddressF,
                GasUsed = 84_000,
                GasUsedTotal = 1_250_000,
                TxHash = TestItem.KeccakG,
                Logs = logs,
            };
        }
    }
}

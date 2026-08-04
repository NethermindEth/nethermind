// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Converters;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class ReceiptsForRpcTests
    {
        [Test]
        public void Are_log_indexes_unique()
        {
            Hash256 txHash = Keccak.OfAnEmptyString;
            LogEntry[] logEntries = { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

            TxReceipt receipt1 = new()
            {
                Bloom = new Bloom(logEntries),
                Index = 1,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = txHash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            UInt256 effectiveGasPrice = new(5526);
            ReceiptForRpc receiptForRpc = new(txHash, receipt1, 0, new(effectiveGasPrice));
            long?[] indexes = receiptForRpc.Logs.Select(static log => log.LogIndex).ToArray();
            long?[] expected = { 0, 1, 2 };

            Assert.That(indexes, Is.EqualTo(expected));
        }

        [Test]
        public void Diagnostic_receipt_json_keeps_block_gas_breakdown()
        {
            TxReceipt receipt = CreateDiagnosticReceipt();
            string serialized = SerializeReceipt(receipt);

            using JsonDocument document = JsonDocument.Parse(serialized);
            JsonElement root = document.RootElement;

            Assert.That(root.GetProperty("effectiveGasPrice").GetString(), Is.EqualTo("0x7"));
            Assert.That(root.GetProperty("blockGasUsed").GetString(), Is.EqualTo("0xa"));
            Assert.That(root.GetProperty("executionGasUsed").GetString(), Is.EqualTo("0xb"));
            Assert.That(root.GetProperty("storageGasUsed").GetString(), Is.EqualTo("0xc"));
        }

        [TestCase("StateGasSpill", "stateGasSpill")]
        [TestCase("StateGasSpillRefunded", "stateGasSpillRefunded")]
        public void Diagnostic_receipt_surface_does_not_include_internal_spill_counters(string clrPropertyName, string jsonPropertyName)
        {
            Assert.That(typeof(TxReceipt).GetProperty(clrPropertyName), Is.Null);

            TxReceipt receipt = CreateDiagnosticReceipt();
            string serialized = SerializeReceipt(receipt);

            using JsonDocument document = JsonDocument.Parse(serialized);
            Assert.That(document.RootElement.TryGetProperty(jsonPropertyName, out _), Is.False);
        }

        [Test]
        public void Error_field_is_not_serialized()
        {
            Hash256 txHash = Keccak.OfAnEmptyString;
            TxReceipt receipt = new()
            {
                Bloom = Bloom.Empty,
                Index = 0,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                GasUsed = 1000,
                TxHash = txHash,
                StatusCode = 0,
                GasUsedTotal = 1000,
                Logs = [],
                Error = "Reverted: INSUFFICIENT_OUTPUT"
            };

            ReceiptForRpc receiptForRpc = new(txHash, receipt, 0, new(new UInt256(1)));
            string json = new EthereumJsonSerializer().Serialize(receiptForRpc);

            Assert.That(json, Does.Not.Contain("\"error\""));
            Assert.That(json, Does.Not.Contain("INSUFFICIENT_OUTPUT"));
        }

        [Test]
        public void Error_field_is_not_deserialized()
        {
            const string json = """
            {
                "transactionHash": "0xc55e2b90168af6972193c1f86fa4d7d7b31a29c156665d15b9cd48618b5177ef",
                "transactionIndex": "0x0",
                "blockHash": "0x0000000000000000000000000000000000000000000000000000000000000001",
                "blockNumber": "0x1",
                "cumulativeGasUsed": "0x3e8",
                "gasUsed": "0x3e8",
                "from": "0x0000000000000000000000000000000000000001",
                "to": "0x0000000000000000000000000000000000000002",
                "contractAddress": null,
                "logs": [],
                "logsBloom": "0x00",
                "status": "0x0",
                "error": "Reverted: INSUFFICIENT_OUTPUT",
                "type": "0x0"
            }
            """;

            ReceiptForRpc? receiptForRpc = new EthereumJsonSerializer().Deserialize<ReceiptForRpc>(json);

            Assert.That(receiptForRpc, Is.Not.Null);
            Assert.That(receiptForRpc!.ToReceipt().Error, Is.Null);
        }

        private static TxReceipt CreateDiagnosticReceipt()
            => new()
            {
                TxType = TxType.EIP1559,
                StatusCode = 1,
                TxHash = TestItem.KeccakA,
                BlockHash = TestItem.KeccakB,
                BlockNumber = 1,
                Index = 2,
                GasUsed = 3,
                GasUsedTotal = 4,
                BlockGasUsed = 10,
                ExecutionGasUsed = 11,
                StorageGasUsed = 12,
                EffectiveGasPrice = new UInt256(7),
                Sender = TestItem.AddressA,
                Recipient = TestItem.AddressB,
                Logs = []
            };

        private static string SerializeReceipt(TxReceipt receipt)
            => new EthereumJsonSerializer(new JsonConverter[] { new TxReceiptConverter() }).Serialize(receipt);
    }
}

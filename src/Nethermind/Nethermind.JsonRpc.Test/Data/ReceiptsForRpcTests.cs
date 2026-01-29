// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
        public void Gas_spent_is_omitted_when_not_enabled()
        {
            Hash256 txHash = Keccak.OfAnEmptyString;
            TxReceipt receipt = new()
            {
                GasUsed = 21000,
                GasUsedTotal = 21000,
                GasSpent = 20000,
                Logs = []
            };

            ReceiptForRpc receiptForRpc = new(txHash, receipt, 0, new(), includeGasSpent: false);

            Assert.That(receiptForRpc.GasSpent, Is.Null);
            string json = new EthereumJsonSerializer().Serialize(receiptForRpc);
            Assert.That(json, Does.Not.Contain("gasSpent"));
        }

        [Test]
        public void Gas_spent_is_included_when_enabled()
        {
            Hash256 txHash = Keccak.OfAnEmptyString;
            TxReceipt receipt = new()
            {
                GasUsed = 21000,
                GasUsedTotal = 21000,
                GasSpent = 20000,
                Logs = []
            };

            ReceiptForRpc receiptForRpc = new(txHash, receipt, 0, new(), includeGasSpent: true);

            Assert.That(receiptForRpc.GasSpent, Is.EqualTo(20000));
            string json = new EthereumJsonSerializer().Serialize(receiptForRpc);
            Assert.That(json, Does.Contain("gasSpent"));
        }
    }
}

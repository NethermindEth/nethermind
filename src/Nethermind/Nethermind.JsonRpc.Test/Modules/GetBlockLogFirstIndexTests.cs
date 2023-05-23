// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class GetBlockLogFirstIndexTests
    {
        [Test]
        public void sum_of_previous_log_indexes_test()
        {
            LogEntry[] logEntries = new[] { Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject };

            TxReceipt receipt1 = new TxReceipt()
            {
                Bloom = new Bloom(logEntries),
                Index = 0,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = TestItem.KeccakA,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt receipt2 = new TxReceipt()
            {
                Bloom = new Bloom(logEntries),
                Index = 1,
                Recipient = TestItem.AddressB,
                Sender = TestItem.AddressD,
                BlockHash = TestItem.KeccakB,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = TestItem.KeccakB,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt receipt3 = new TxReceipt()
            {
                Bloom = new Bloom(logEntries),
                Index = 2,
                Recipient = TestItem.AddressC,
                Sender = TestItem.AddressD,
                BlockHash = TestItem.KeccakC,
                BlockNumber = 2,
                ContractAddress = TestItem.AddressC,
                GasUsed = 1000,
                TxHash = TestItem.KeccakC,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = logEntries
            };

            TxReceipt[] receipts = { receipt1, receipt2, receipt3 };
            int index = 2;

            int sum = receipts.GetBlockLogFirstIndex(index);

            Assert.That(sum, Is.EqualTo(4));
        }
    }
}

//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ReceiptsForRpcTests : SerializationTestBase
    {
        [Test]
        public void Are_log_indexes_unique()
        {
            Keccak txHash = Keccak.OfAnEmptyString;
            LogEntry[] logEntries = {Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject};
            
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
            ReceiptForRpc receiptForRpc = new(txHash, receipt1, effectiveGasPrice);
            long?[] indexes = receiptForRpc.Logs.Select(log => log.LogIndex).ToArray();
            long?[] expected = {0, 1, 2};
            
            Assert.AreEqual(expected, indexes);
        }
    }
}

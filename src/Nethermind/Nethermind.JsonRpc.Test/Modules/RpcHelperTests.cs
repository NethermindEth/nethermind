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

using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules
{
    [TestFixture]
    public class RpcHelperTests
    {
        
        [Test]
        public async Task sum_of_previous_log_indexes_test()
        {
            LogEntry[] logEntries = new[] {Build.A.LogEntry.TestObject, Build.A.LogEntry.TestObject};
            
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
            
            TxReceipt[] receipts = {receipt1, receipt2, receipt3};
            int index = 2;
            
            int sum = receipts.GetBlockLogFirstIndex(index);
            
            Assert.AreEqual(sum, 4);
        }
    }
}

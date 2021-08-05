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
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth.FeeHistory;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Test.Modules.FeeHistoryOracleTests;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [TestCase("2", "earliest", "[0,10.5,20,30,40]", "0x3635c9adc5dea00000")]
        [TestCase("3", "latest", "[0,10.5,20,30,40]", "0x3635c9adc5de9f09e5")]
        [TestCase("1", "pending", "[0,10.5,20,30,40]", "0x3635c9adc5de9f09e5")]
        [TestCase("2", "0x01", "[0,10.5,20,30,40]", "0x3635c9adc5dea00000")]
        public async Task Eth_feeHistory(string blockCount, string blockParameter, string rewardPercentiles,
            string expectedResult)
        {
            using Context ctx = await Context.Create();
            FeeHistoryOracle feeHistoryOracle = GetTestFeeHistoryOracle();
            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithFeeHistoryOracle(feeHistoryOracle).Build();
            string serialized = ctx._test.TestEthRpc("eth_feeHistory", blockCount, blockParameter, rewardPercentiles);
            serialized.Should().Be($"{{\"jsonrpc\":\"2.0\",\"result\":\"{expectedResult}\",\"id\":67}}");
        }

        public FeeHistoryOracle GetTestFeeHistoryOracle()
        {
            Transaction tx1FirstBlock = Build.A.Transaction.WithGasPrice(3).TestObject; //Reward: Min (3, 3-2) => 1 
            Transaction tx2FirstBlock = Build.A.Transaction.WithGasPrice(4).TestObject;
            Transaction tx1SecondBlock = Build.A.Transaction.WithGasPrice(2).TestObject; //Reward: BaseFee > FeeCap => 0
            Transaction tx2SecondBlock = Build.A.Transaction.WithGasPrice(5).TestObject;
            Block firstBlock = Build.A.Block.Genesis.WithBaseFeePerGas(2).WithGasUsed(3).WithGasLimit(5)
                .WithTransactions(tx1FirstBlock, tx2FirstBlock).TestObject;
            Block secondBlock = Build.A.Block.WithNumber(1).WithBaseFeePerGas(3).WithGasUsed(2).WithGasLimit(8)
                .WithTransactions(tx1SecondBlock, tx2SecondBlock).TestObject;
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            BlockParameter newestBlockParameter = new(1);
            blockFinder.FindBlock(newestBlockParameter).Returns(secondBlock);
            blockFinder.FindParent(secondBlock, BlockTreeLookupOptions.RequireCanonical).Returns(firstBlock);
            blockFinder.FindBlock(BlockParameter.Earliest).Returns(firstBlock);
            blockFinder.FindBlock(BlockParameter.Latest).Returns(secondBlock);
            blockFinder.FindBlock(BlockParameter.Pending).Returns((Block?) null);
            
            IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();
            receiptStorage.Get(firstBlock).Returns(new TxReceipt[]
            {
                new() {GasUsed = 3},
                new() {GasUsed = 4}
            });
            receiptStorage.Get(firstBlock).Returns(new TxReceipt[]
            {
                new() {GasUsed = 2},
                new() {GasUsed = 3}
            });
            FeeHistoryOracle feeHistoryOracle =
                GetSubstitutedFeeHistoryOracle(blockFinder: blockFinder, receiptStorage: receiptStorage);

            return feeHistoryOracle;
        }
    }
}

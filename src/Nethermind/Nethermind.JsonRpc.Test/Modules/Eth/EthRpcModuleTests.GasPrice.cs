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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.Wallet;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    public partial class EthRpcModuleTests
    {
        [Test]
        public async Task Eth_gasPrice_WhenHeadBlockIsNull_ThrowsException()
        {
            using Context ctx = await Context.Create();
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            blockFinder.FindHeadBlock().Returns((Block) null);
            
            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder).Build();
            string serialized = ctx._test.TestEthRpc("eth_gasPrice");
            
            Assert.AreEqual("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Head Block was not found.\"},\"id\":67}",
                serialized);
        }
        
        [Test]
        public async Task Eth_gasPrice_GivenValidHeadBlock_CallsGasPriceEstimateFromGasPriceOracle()
        {
            Context ctx = await Context.Create(); 
            IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
            IGasPriceOracle gasPriceOracle = Substitute.For<IGasPriceOracle>();
            Block testBlock = Build.A.Block.Genesis.TestObject;
            blockFinder.FindHeadBlock().Returns(testBlock);
            blockFinder.FindBlock(Arg.Is<long>(a => a == 0)).Returns(testBlock);
            
            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockFinder)
                .WithGasPriceOracle(gasPriceOracle).Build();
            ctx._test.TestEthRpc("eth_gasPrice");
            
            gasPriceOracle.Received(1).GasPriceEstimate(Arg.Any<Block>(), Arg.Any<IBlockFinder>());
        }

        [Test]
        public async Task Eth_gasPrice_BlocksAvailableLessThanBlocksToCheck_ShouldGiveCorrectResult()
        {
            Context ctx = await Context.Create();
            Block[] blocks = GetThreeTestBlocks();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();

            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 4, specProvider: specProvider);
            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTreeSetup.BlockTree)
                .Build();
            
            string serialized = ctx._test.TestEthRpc("eth_gasPrice"); //Gas Prices: 1,2,3,4,5,6 | Max Index: 5 | 60th Percentile: 5 * (3/5) = 3 | Result: 4 (0x4) 
            Assert.AreEqual($"{{\"jsonrpc\":\"2.0\",\"result\":\"0x4\",\"id\":67}}", serialized);
        }

        [Test]
        public async Task Eth_gasPrice_NumTxInMinBlocksGreaterThanBlockLimit_GetTxFromBlockLimitBlocks()
        {
            Context ctx = await Context.Create();
            Block[] blocks = GetThreeTestBlocks();
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            BlockTreeSetup blockTreeSetup = new BlockTreeSetup(blocks: blocks, blockLimit: 2, specProvider: specProvider);

            ctx._test = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).WithBlockFinder(blockTreeSetup.BlockTree)
                .WithGasPriceOracle(blockTreeSetup.GasPriceOracle).Build();
            ctx._test.TestEthRpc("eth_gasPrice");
            
            List<UInt256> expected = new List<UInt256>{3, 4, 5, 6};
            blockTreeSetup.GasPriceOracle.TxGasPriceList.Should().Equal(expected);
        }

        private static Block[] GetThreeTestBlocks()
        {
            Block firstBlock = Build.A.Block.WithNumber(0).WithParentHash(Keccak.Zero).WithTransactions(
                Build.A.Transaction.WithGasPrice(1).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(2).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(0).TestObject
            ).TestObject;

            Block secondBlock = Build.A.Block.WithNumber(1).WithParentHash(firstBlock.Hash!).WithTransactions(
                Build.A.Transaction.WithGasPrice(3).SignedAndResolved(TestItem.PrivateKeyC).WithNonce(0).TestObject,
                Build.A.Transaction.WithGasPrice(4).SignedAndResolved(TestItem.PrivateKeyD).WithNonce(0).TestObject
            ).TestObject;

            Block thirdBlock = Build.A.Block.WithNumber(2).WithParentHash(secondBlock.Hash!).WithTransactions(
                Build.A.Transaction.WithGasPrice(5).SignedAndResolved(TestItem.PrivateKeyA).WithNonce(1).TestObject,
                Build.A.Transaction.WithGasPrice(6).SignedAndResolved(TestItem.PrivateKeyB).WithNonce(1).TestObject
            ).TestObject;
           
            return new[]{firstBlock, secondBlock, thirdBlock};
        }
        
        public class TestEthRpcModule : EthRpcModule
        {
            public TestEthRpcModule(
                IJsonRpcConfig rpcConfig,
                IBlockchainBridge blockchainBridge,
                IBlockFinder blockFinder,
                IStateReader stateReader,
                ITxPool txPool,
                ITxSender txSender,
                IWallet wallet,
                ILogManager logManager,
                IGasPriceOracle gasPriceOracle)
                : base(
                    rpcConfig, 
                    blockchainBridge, 
                    blockFinder, 
                    stateReader, 
                    txPool, 
                    txSender, 
                    wallet, 
                    logManager,
                    gasPriceOracle)
            {
            }

            public void SetGasPriceOracle(IGasPriceOracle gasPriceOracle)
            {
                GasPriceOracle = gasPriceOracle;
            }
        }
        private static TestEthRpcModule GetTestEthRpcModule(IBlockFinder blockFinder = null)
        {
            return new TestEthRpcModule
            (
                Substitute.For<IJsonRpcConfig>(),
                Substitute.For<IBlockchainBridge>(),
                blockFinder ?? Substitute.For<IBlockFinder>(),
                Substitute.For<IStateReader>(),
                Substitute.For<ITxPool>(),
                Substitute.For<ITxSender>(),
                Substitute.For<IWallet>(),
                Substitute.For<ILogManager>(),
                Substitute.For<IGasPriceOracle>()
            );
        }
    }
}

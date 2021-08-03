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

#nullable enable
using System;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.JsonRpc.Modules.Eth.EthRpcModule.FeeHistoryOracle;

namespace Nethermind.JsonRpc.Test.Modules.Eth
{
    partial class EthRpcModuleTests
    {
        [Test]
        public void GetEffectiveGasPriceAndRewards_IfTxsInBlock_GasUsedInsertedCorrectly()
        {
            var (blockchainBridge, transactions) = GetTestBlockchainBridgeAndTxsA();
            RewardInsertionManager rewardInsertionManager = new(blockchainBridge);
            BlockFeeInfo blockFeeInfo = new() {BaseFee = 3, Block = Build.A.Block.WithTransactions(transactions).TestObject};
            long[] expectedGasUsed = {5,7,9,11};
            
            GasUsedAndReward[] gasUsedAndRewards = rewardInsertionManager.GetEffectiveGasPriceAndRewards(blockFeeInfo);

            long[] gasUsed = gasUsedAndRewards.Select(g => g.GasUsed).ToArray();
            gasUsed.Should().BeEquivalentTo(expectedGasUsed);
        }

        [Test]
        public void GetEffectiveGasPriceAndRewards_IfTxsInBlock_RewardsInsertedCorrectly()
        {
            var (blockchainBridge, transactions) = GetTestBlockchainBridgeAndTxsA();
            RewardInsertionManager rewardInsertionManager = new(blockchainBridge);
            BlockFeeInfo blockFeeInfo = new() {BaseFee = 3, Block = Build.A.Block.WithTransactions(transactions).TestObject};
            UInt256[] expectedRewards = {1,2,2,3};
            
            GasUsedAndReward[] gasUsedAndRewards = rewardInsertionManager.GetEffectiveGasPriceAndRewards(blockFeeInfo);

            UInt256[] rewards = gasUsedAndRewards.Select(g => g.Reward).ToArray();
            rewards.Should().BeEquivalentTo(expectedRewards);
        }
        
        [Test]
        public void GetEffectiveGasPriceAndRewards_IfTxsRewardsOutOfOrder_RewardsInsertedInOrder()
        {
            var (blockchainBridge, transactions) = GetTestBlockchainBridgeAndTxsB();
            RewardInsertionManager rewardInsertionManager = new(blockchainBridge);
            BlockFeeInfo blockFeeInfo = new() {BaseFee = 3, Block = Build.A.Block.WithTransactions(transactions).TestObject};
            UInt256[] expectedRewards = {7,10,13,22};
            long[] expectedGasUsed = {5,10,15,20};
            
            GasUsedAndReward[] gasUsedAndRewards = rewardInsertionManager.GetEffectiveGasPriceAndRewards(blockFeeInfo);
            
            UInt256[] rewards = gasUsedAndRewards.Select(g => g.Reward).ToArray();
            long[] gasUsed = gasUsedAndRewards.Select(g => g.GasUsed).ToArray();
            rewards.Should().BeEquivalentTo(expectedRewards);
            gasUsed.Should().BeEquivalentTo(expectedGasUsed);
        }

        [TestCase(100, new double[]{10,20,50,70,90}, new ulong[]{1,3,5,7,7})]
        [TestCase(250, new double[]{1.5, 10, 13.75, 50.5, 80}, new ulong[]{1,3,5,7,7})]
        public void GetRewardsAtPercentiles_GivenValidInputs_CalculatesPercentilesCorrectly(long gasUsed, double[] rewardPercentiles, ulong[] expected)
        {
            BlockFeeInfo blockFeeInfo = new(){Block = Build.A.Block.WithGasUsed(gasUsed).TestObject};
            GasUsedAndReward[] gasUsedAndRewards = 
            {                             
                new(10, 1),
                new(20, 3),
                new(30, 5),
                new(40, 7) 
            };
            UInt256[] expectedUInt256 = expected.Select(n => (UInt256) n).ToArray();

            RewardInsertionManager rewardInsertionManager = new(Substitute.For<IBlockchainBridge>());
            UInt256[] result = rewardInsertionManager.GetRewardsAtPercentiles(blockFeeInfo, rewardPercentiles, gasUsedAndRewards);
            result.Should().BeEquivalentTo(expectedUInt256);
        }

        [TestCase(30, new double[] {20,40,60,80.5}, new ulong[]{10,10,13,13})]
        [TestCase(40, new double[] {20,40,60,80.5}, new ulong[]{10,13,13,22})]
        [TestCase(40, new double[] {10,20,30,40}, new ulong[]{7,10,10,13})]
        public void CalculateAndInsertRewards_GivenValidInputs_CalculatesPercentilesCorrectly(long gasUsed, double[] rewardPercentiles, ulong[] expected)
        {
            var (blockchainBridge, transactions) = GetTestBlockchainBridgeAndTxsB();
            RewardInsertionManager rewardInsertionManager = new(blockchainBridge);
            BlockFeeInfo blockFeeInfo = new() {BaseFee = 3, Block = Build.A.Block.WithTransactions(transactions).WithGasUsed(gasUsed).TestObject};
            UInt256[] expectedUInt256 = expected.Select(n => (UInt256) n).ToArray();
            
            UInt256[]? gasUsedAndRewards = rewardInsertionManager.CalculateAndInsertRewards(blockFeeInfo, rewardPercentiles);

            gasUsedAndRewards.Should().BeEquivalentTo(expectedUInt256);
        }
        
        private (IBlockchainBridge blockchainBridge, Transaction[] transactions) GetTestBlockchainBridgeAndTxsA()
        {
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            Transaction[] transactions = new Transaction[]
            {                                                                                                                                       //Rewards: 
                Build.A.Transaction.WithHash(TestItem.KeccakA).WithMaxFeePerGas(5).WithMaxPriorityFeePerGas(1).WithType(TxType.EIP1559).TestObject, //1
                Build.A.Transaction.WithHash(TestItem.KeccakB).WithMaxFeePerGas(5).WithMaxPriorityFeePerGas(3).WithType(TxType.EIP1559).TestObject, //2
                Build.A.Transaction.WithHash(TestItem.KeccakC).WithMaxFeePerGas(6).WithMaxPriorityFeePerGas(2).WithType(TxType.EIP1559).TestObject, //2
                Build.A.Transaction.WithHash(TestItem.KeccakD).WithMaxFeePerGas(6).WithMaxPriorityFeePerGas(3).WithType(TxType.EIP1559).TestObject  //3
            };
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakA))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(0, transactions[0], 5), 1));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakB))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(1, transactions[1], 7), 2));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakC))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(2, transactions[2], 9), 3));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakD))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(3, transactions[3], 11), 4));
            return (blockchainBridge, transactions);
        }

        private (IBlockchainBridge blockchainBridge, Transaction[] transactions) GetTestBlockchainBridgeAndTxsB()
        {
            IBlockchainBridge blockchainBridge = Substitute.For<IBlockchainBridge>();
            Transaction[] transactions = new Transaction[]
            {                                                                                                                                         //Rewards: 
                Build.A.Transaction.WithHash(TestItem.KeccakA).WithMaxFeePerGas(20).WithMaxPriorityFeePerGas(13).WithType(TxType.EIP1559).TestObject, //13
                Build.A.Transaction.WithHash(TestItem.KeccakB).WithMaxFeePerGas(10).WithMaxPriorityFeePerGas(7).WithType(TxType.EIP1559).TestObject,  //7
                Build.A.Transaction.WithHash(TestItem.KeccakC).WithMaxFeePerGas(25).WithMaxPriorityFeePerGas(24).WithType(TxType.EIP1559).TestObject, //22
                Build.A.Transaction.WithHash(TestItem.KeccakD).WithMaxFeePerGas(15).WithMaxPriorityFeePerGas(10).WithType(TxType.EIP1559).TestObject  //10
            };
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakA))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(0, transactions[0], 15), 1));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakB))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(1, transactions[1], 5), 2));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakC))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(2, transactions[2], 20), 3));
            blockchainBridge
                .GetReceiptAndEffectiveGasPrice(Arg.Is<Keccak>(k => k == TestItem.KeccakD))
                .Returns(((TxReceipt Receipt, UInt256? EffectiveGasPrice)) (GetTxReceipt(3, transactions[3], 10), 4));
            return (blockchainBridge, transactions);
        }

        private TxReceipt GetTxReceipt(int index, Transaction transaction, long gasUsed)
        {
            return new()
            {
                Bloom = new Bloom(),
                Index = index,
                Recipient = TestItem.AddressA,
                Sender = TestItem.AddressB,
                BlockHash = TestItem.KeccakA,
                BlockNumber = 1,
                ContractAddress = TestItem.AddressC,
                GasUsed = gasUsed,
                TxHash = transaction.Hash,
                StatusCode = 0,
                GasUsedTotal = 2000,
                Logs = null
            };
        }
    }
}

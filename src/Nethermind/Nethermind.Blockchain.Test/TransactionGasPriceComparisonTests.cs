// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    public class TransactionComparisonTests
    {
        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, 10, 0)]
        [TestCase(15, 10, -1)]
        [TestCase(2, 3, 1)]
        public void GasPriceComparer_for_legacy_transactions(int gasPriceX, int gasPriceY, int expectedResult)
        {
            TestingContext context = new();
            IComparer<Transaction> comparer = context.DefaultComparer;
            AssertLegacyTransactions(comparer, gasPriceX, gasPriceY, expectedResult);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, 10, 0)]
        [TestCase(15, 10, -1)]
        [TestCase(2, 3, 1)]
        public void ProducerGasPriceComparer_for_legacy_transactions(int gasPriceX, int gasPriceY, int expectedResult)
        {
            TestingContext context = new();
            IComparer<Transaction> comparer = context.GetProducerComparer(new BlockPreparationContext(0, 0));
            AssertLegacyTransactions(comparer, gasPriceX, gasPriceY, expectedResult);
        }

        [Timeout(Timeout.MaxTestTime)]
        // head block number before eip 1559 transition
        [TestCase(10, 10, 0, 0, 0)]
        [TestCase(15, 10, 10, 1, -1)]
        [TestCase(2, 3, 20, 0, 1)]
        // head block number after eip 1559 transition
        [TestCase(10, 10, 16, 5, 0)]
        [TestCase(15, 10, 11, 6, -1)]
        [TestCase(2, 3, 33, 7, 1)]
        public void GasPriceComparer_for_legacy_transactions_1559(int gasPriceX, int gasPriceY, int headBaseFee, long headBlockNumber, int expectedResult)
        {
            long eip1559Transition = 5;
            TestingContext context = new TestingContext(true, eip1559Transition)
                .WithHeadBaseFeeNumber((UInt256)headBaseFee)
                .WithHeadBlockNumber(headBlockNumber);
            IComparer<Transaction> comparer = context.DefaultComparer;
            AssertLegacyTransactions(comparer, gasPriceX, gasPriceY, expectedResult);
        }

        [Timeout(Timeout.MaxTestTime)]
        // head block number before eip 1559 transition
        [TestCase(10, 10, 0, 0, 0)]
        [TestCase(15, 10, 10, 1, -1)]
        [TestCase(2, 3, 20, 0, 1)]
        // head block number after eip 1559 transition
        [TestCase(10, 10, 16, 5, 0)]
        [TestCase(15, 10, 11, 6, -1)]
        [TestCase(2, 3, 33, 7, 1)]
        public void ProducerGasPriceComparer_for_legacy_transactions_1559(int gasPriceX, int gasPriceY, int headBaseFee, long headBlockNumber, int expectedResult)
        {
            long eip1559Transition = 5;
            TestingContext context = new(true, eip1559Transition);
            IComparer<Transaction> comparer = context.GetProducerComparer(new BlockPreparationContext(0, 0));
            AssertLegacyTransactions(comparer, gasPriceX, gasPriceY, expectedResult);
        }

        private void AssertLegacyTransactions(IComparer<Transaction> comparer, int gasPriceX, int gasPriceY, int expectedResult)
        {
            Transaction x = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithGasPrice((UInt256)gasPriceX).TestObject;
            Transaction y = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithGasPrice((UInt256)gasPriceY).TestObject;
            int result = comparer.Compare(x, y);
            Assert.AreEqual(expectedResult, result);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, 5, 12, 4, 4, 6, -1)]
        [TestCase(10, 5, 12, 4, 10, 6, 1)]
        [TestCase(10, 4, 12, 4, 4, 6, 1)]
        [TestCase(12, 4, 12, 4, 4, 6, 0)]
        [TestCase(10, 5, 12, 4, 4, 3, -1)]
        [TestCase(10, 5, 12, 4, 10, 3, -1)]
        public void GasPriceComparer_for_eip1559_transactions(int feeCapX, int gasPremiumX, int feeCapY, int gasPremiumY, int headBaseFee, long headBlockNumber, int expectedResult)
        {
            long eip1559Transition = 5;
            TestingContext context = new TestingContext(true, eip1559Transition)
                .WithHeadBaseFeeNumber((UInt256)headBaseFee)
                .WithHeadBlockNumber(headBlockNumber);
            IComparer<Transaction> comparer = context.DefaultComparer;
            Assert1559Transactions(comparer, feeCapX, gasPremiumX, feeCapY, gasPremiumY, expectedResult);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(4, 3, 1, 3, 1)]
        [TestCase(4, 3, 3, 1, -1)]
        [TestCase(4, 3, 0, 0, 0)]
        public void GasPriceComparer_use_gas_bottleneck_when_it_is_not_null(int gasPriceX, int gasPriceY, int gasBottleneckX, int gasBottleneckY, int expectedResult)
        {
            long eip1559Transition = long.MaxValue;
            TestingContext context = new TestingContext(false, eip1559Transition)
                .WithHeadBaseFeeNumber((UInt256)0)
                .WithHeadBlockNumber(1);
            IComparer<Transaction> comparer = context.DefaultComparer;
            Transaction x = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithGasPrice((UInt256)gasPriceX).WithGasBottleneck((UInt256)gasBottleneckX).TestObject;
            Transaction y = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithGasPrice((UInt256)gasPriceY).WithGasBottleneck((UInt256)gasBottleneckY).TestObject;
            int result = comparer.Compare(x, y);
            Assert.AreEqual(expectedResult, result);
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, 5, 12, 4, 4, 6, -1)]
        [TestCase(10, 5, 12, 4, 10, 6, 1)]
        [TestCase(10, 4, 12, 4, 4, 6, 1)]
        [TestCase(12, 4, 12, 4, 4, 6, 0)]
        [TestCase(10, 5, 12, 4, 4, 3, -1)]
        [TestCase(10, 5, 12, 4, 10, 3, -1)]
        public void ProducerGasPriceComparer_for_eip1559_transactions_1559(int feeCapX, int gasPremiumX, int feeCapY, int gasPremiumY, int headBaseFee, long headBlockNumber, int expectedResult)
        {
            long eip1559Transition = 5;
            TestingContext context = new(true, eip1559Transition);
            IComparer<Transaction> comparer = context.GetProducerComparer(new BlockPreparationContext((UInt256)headBaseFee, headBlockNumber));
            Assert1559Transactions(comparer, feeCapX, gasPremiumX, feeCapY, gasPremiumY, expectedResult);
        }

        private void Assert1559Transactions(IComparer<Transaction> comparer, int feeCapX, int gasPremiumX, int feeCapY, int gasPremiumY, int expectedResult)
        {
            Transaction x = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithMaxFeePerGas((UInt256)feeCapX).WithMaxPriorityFeePerGas((UInt256)gasPremiumX)
                .WithType(TxType.EIP1559).TestObject;
            Transaction y = Build.A.Transaction.WithSenderAddress(TestItem.AddressA)
                .WithMaxFeePerGas((UInt256)feeCapY).WithMaxPriorityFeePerGas((UInt256)gasPremiumY)
                .WithType(TxType.EIP1559).TestObject;
            int result = comparer.Compare(x, y);
            Assert.AreEqual(expectedResult, result);
        }

        private class TestingContext
        {
            private readonly IBlockTree _blockTree;
            private readonly ITransactionComparerProvider _transactionComparerProvider;
            private long _blockNumber;
            private UInt256 _baseFee;

            public TestingContext(bool isEip1559Enabled = false, long eip1559TransitionBlock = 0)
            {
                ReleaseSpec releaseSpec = new();
                ReleaseSpec eip1559ReleaseSpec = new() { IsEip1559Enabled = isEip1559Enabled, Eip1559TransitionBlock = eip1559TransitionBlock };
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpecFor1559(Arg.Is<long>(x => x >= eip1559TransitionBlock)).Returns(eip1559ReleaseSpec);
                specProvider.GetSpecFor1559(Arg.Is<long>(x => x < eip1559TransitionBlock)).Returns(releaseSpec);
                specProvider.GetSpec(Arg.Is<BlockHeader>(x => x.Number >= eip1559TransitionBlock)).Returns(eip1559ReleaseSpec);
                specProvider.GetSpec(Arg.Is<BlockHeader>(x => x.Number < eip1559TransitionBlock)).Returns(releaseSpec);
                _blockTree = Substitute.For<IBlockTree>();
                UpdateBlockTreeHead();
                _transactionComparerProvider =
                    new TransactionComparerProvider(specProvider, _blockTree);
            }

            public IComparer<Transaction> DefaultComparer => _transactionComparerProvider.GetDefaultComparer();

            public IComparer<Transaction> GetProducerComparer(BlockPreparationContext blockPreparationContext)
            {
                return _transactionComparerProvider.GetDefaultProducerComparer(blockPreparationContext);
            }

            public TestingContext WithHeadBlockNumber(long headBlockNumber)
            {
                _blockNumber = headBlockNumber;
                UpdateBlockTreeHead();
                return this;
            }

            public TestingContext WithHeadBaseFeeNumber(UInt256 headBaseFee)
            {
                _baseFee = headBaseFee;
                UpdateBlockTreeHead();
                return this;
            }

            private void UpdateBlockTreeHead()
            {
                Block block = Build.A.Block
                    .WithNumber(_blockNumber)
                    .WithBaseFeePerGas(_baseFee).TestObject;
                _blockTree.Head.Returns(block);
            }
        }
    }
}

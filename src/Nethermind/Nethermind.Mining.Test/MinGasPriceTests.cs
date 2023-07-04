// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class MinGasPriceTests
    {
        [TestCase(0L, 0L, true)]
        [TestCase(1L, 0L, false)]
        [TestCase(1L, 1L, true)]
        [TestCase(1L, 2L, true)]
        [TestCase(2L, 1L, false)]
        public void Test(long minimum, long actual, bool expectedResult)
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(new ReleaseSpec()
            {
                IsEip1559Enabled = false
            });
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = (UInt256)minimum
            };
            MinGasPriceTxFilter _filter = new(blocksConfig, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice((UInt256)actual).TestObject;
            _filter.IsAllowed(tx, null).Equals(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow).Should().BeTrue();
        }

        [TestCase(0L, 0L, 0L, true)]
        [TestCase(1L, 0L, 0L, false)]
        [TestCase(1L, 0L, 1L, false)]
        [TestCase(1L, 100L, 1000L, false)]
        [TestCase(1L, 875L, 1000L, false)]
        [TestCase(1L, 876L, 1000L, true)]
        [TestCase(1L, 876L, 0L, false)]
        [TestCase(2L, 1000L, 1L, false)]
        [TestCase(2L, 1000L, 1000L, true)]
        public void Test1559(long minimum, long maxFeePerGas, long maxPriorityFeePerGas, bool expectedResult)
        {
            ISpecProvider specProvider = Substitute.For<ISpecProvider>();
            specProvider.GetSpec(Arg.Any<long>(), Arg.Any<ulong>()).IsEip1559Enabled.Returns(true);
            specProvider.GetSpec(Arg.Any<BlockHeader>()).IsEip1559Enabled.Returns(true);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = (UInt256)minimum
            };
            MinGasPriceTxFilter _filter = new(blocksConfig, specProvider);
            Transaction tx = Build.A.Transaction.WithGasPrice(0)
                .WithMaxFeePerGas((UInt256)maxFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithType(TxType.EIP1559).TestObject;
            BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000).WithBaseFeePerGas((UInt256)1000);
            _filter.IsAllowed(tx, blockBuilder.TestObject.Header).Equals(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow).Should().BeTrue();
        }
    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mining.Test
{
    [TestFixture]
    public class MinGasPriceTests
    {
        [Test]
        public void Rejection_preserves_detailed_message([Values] bool customFloor)
        {
            MinGasPriceTxFilter filter = new(new BlocksConfig { MinGasPrice = 2 });
            Transaction tx = Build.A.Transaction.WithGasPrice(1).TestObject;
            IReleaseSpec spec = new ReleaseSpec { IsEip1559Enabled = false };

            AcceptTxResult result = customFloor
                ? filter.IsAllowed(tx, null, 3, spec)
                : filter.IsAllowed(tx, null!, spec);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.EqualTo(AcceptTxResult.FeeTooLow));
                Assert.That(result.ToString(), Is.EqualTo(
                    $"{AcceptTxResult.FeeTooLow}, EffectivePriorityFeePerGas too low 1 < {(customFloor ? 3 : 2)}, BaseFee: 0"));
            }
        }

        [Test]
        public void Selection_does_not_allocate_for_gas_price_filter([Values(0, 1, 2)] int gasPrice)
        {
            ITxFilterPipeline pipeline = new TxFilterPipelineBuilder(NullLogManager.Instance)
                .WithMinGasPriceFilter(new BlocksConfig { MinGasPrice = 1 })
                .Build;
            Transaction tx = Build.A.Transaction.WithGasPrice((UInt256)gasPrice).TestObject;
            IReleaseSpec spec = new ReleaseSpec { IsEip1559Enabled = false };

            for (int i = 0; i < 100; i++)
            {
                pipeline.Execute(tx, null!, spec);
            }

            bool accepted = true;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                accepted &= pipeline.Execute(tx, null!, spec);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(accepted, Is.EqualTo(gasPrice >= 1));
                Assert.That(allocated, Is.Zero);
            }
        }

        [TestCase(0L, 0L, true)]
        [TestCase(1L, 0L, false)]
        [TestCase(1L, 1L, true)]
        [TestCase(1L, 2L, true)]
        [TestCase(2L, 1L, false)]
        public void Test(long minimum, long actual, bool expectedResult)
        {
            IReleaseSpec releaseSpec = new ReleaseSpec()
            {
                IsEip1559Enabled = false
            };

            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = (UInt256)minimum
            };

            MinGasPriceTxFilter filter = new(blocksConfig);
            Transaction tx = Build.A.Transaction.WithGasPrice((UInt256)actual).TestObject;
            Assert.That(filter.IsAllowed(tx, null!, releaseSpec).Equals(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow), Is.True);
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
            specProvider.GetSpec(Arg.Any<ForkActivation>()).IsEip1559Enabled.Returns(true);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).BaseFeeCalculator.Returns(new DefaultBaseFeeCalculator());

            specProvider.GetSpec(Arg.Any<ForkActivation>()).ForkBaseFee.Returns(Eip1559Constants.DefaultForkBaseFee);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).BaseFeeMaxChangeDenominator.Returns(Eip1559Constants.DefaultBaseFeeMaxChangeDenominator);
            specProvider.GetSpec(Arg.Any<ForkActivation>()).ElasticityMultiplier.Returns(Eip1559Constants.DefaultElasticityMultiplier);

            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = (UInt256)minimum
            };
            MinGasPriceTxFilter _filter = new(blocksConfig);
            Transaction tx = Build.A.Transaction.WithGasPrice(0)
                .WithMaxFeePerGas((UInt256)maxFeePerGas)
                .WithMaxPriorityFeePerGas((UInt256)maxPriorityFeePerGas)
                .WithType(TxType.EIP1559).TestObject;
            BlockBuilder blockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithGasLimit(10000).WithBaseFeePerGas((UInt256)1000);
            Assert.That(_filter.IsAllowed(tx, blockBuilder.TestObject.Header, specProvider.GetSpec(blockBuilder.TestObject.Header)).Equals(expectedResult ? AcceptTxResult.Accepted : AcceptTxResult.FeeTooLow), Is.True);
        }
    }
}

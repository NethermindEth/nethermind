// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBaseFeeCalculatorTests
{
    /// <remarks>
    /// Tests sourced from <see href="https://github.com/ethereum-optimism/op-geth/blob/1e60ba82d31bc17111481998100cd948ee06c0ab/consensus/misc/eip1559/eip1559_test.go#L191"/>
    /// </remarks>
    [TestCase(15_000_000UL, 10_000_000, 10u, 2u)] // Target
    [TestCase(10_000_000UL, 9_666_667, 10u, 2u)] // Below
    [TestCase(20_000_000UL, 10_333_333, 10u, 2u)] // Above
    [TestCase(3_000_000UL, 10_000_000, 2u, 10u)] // Target
    [TestCase(1_000_000UL, 6_666_667, 2u, 10u)] // Below
    [TestCase(30_000_000UL, 55_000_000, 2u, 10u)] // Above
    public void CalculatesBaseFee_AfterHolocene_UsingExtraDataParameters(ulong gasUsed, long expectedBaseFee, UInt32 denominator, UInt32 elasticity)
    {
        const ulong HoloceneTimestamp = 10_000_000;

        IReleaseSpec releaseSpec = new OptimismReleaseSpec
        {
            IsEip1559Enabled = true,
            IsOpHoloceneEnabled = true,
            BaseFeeCalculator = new OptimismBaseFeeCalculator(HoloceneTimestamp, null, new DefaultBaseFeeCalculator())
        };

        EIP1559Parameters parameters = new(0, denominator, elasticity);
        byte[] extraData = new byte[parameters.ByteLength];
        parameters.WriteTo(extraData);

        BlockHeader blockHeader = Build.A.BlockHeader
            .WithGasLimit(30_000_000)
            .WithBaseFee(10_000_000)
            .WithTimestamp(HoloceneTimestamp)
            .WithGasUsed(gasUsed)
            .WithExtraData(extraData)
            .TestObject;

        UInt256 actualBaseFee = BaseFeeCalculator.CalculateForReferenceSlot(blockHeader, releaseSpec);

        Assert.That(actualBaseFee, Is.EqualTo((UInt256)expectedBaseFee));
    }

    private static class JovianTest
    {
        public const ulong GasLimit = 30_000_000;
        public const uint Denominator = 50;
        public const uint Elasticity = 3;
        public const ulong GasTarget = GasLimit / Elasticity;
    }

    /// <remarks>
    /// Tests sourced from <see href="https://github.com/ethereum-optimism/op-geth/blob/6658a36c9862694c82eabd803ca885370667401f/consensus/misc/eip1559/eip1559_test.go#L235"/>
    /// </remarks>
    [TestCase(1L, JovianTest.GasTarget - 1_000_000, 0UL, Spec.HoloceneTimeStamp, 1_000_000_000, 1L)]
    [TestCase(1L, JovianTest.GasTarget, 0UL, Spec.JovianTimeStamp, 1_000_000_000, 1_000_000_000)]
    [TestCase(1L, JovianTest.GasTarget + 1_000_000, 0UL, Spec.JovianTimeStamp, 1_000_000_000, 1_000_000_000)]
    [TestCase(2_000_000_000, JovianTest.GasTarget + 10_000_000, 0UL, Spec.JovianTimeStamp, 1_000_000_000, 2_040_000_000)]
    [TestCase(1, JovianTest.GasTarget - 1_000_000, 0UL, Spec.JovianTimeStamp, 1_000_000_000, 1_000_000_000)]
    [TestCase(2_097_152, JovianTest.GasTarget - 1_000_000, 0UL, Spec.JovianTimeStamp, 2_000_000, 2_092_958)]
    [TestCase(10_000, JovianTest.GasTarget - 1, 0UL, Spec.JovianTimeStamp, 10_000, 10_000)]
    [TestCase(10_000, JovianTest.GasTarget + 1, 0UL, Spec.JovianTimeStamp, 10_000, 10_000 + 1)]
    [TestCase(10_000, JovianTest.GasTarget, JovianTest.GasLimit, Spec.HoloceneTimeStamp, 1_000_000, 10_000)]
    [TestCase(10_000, JovianTest.GasTarget, JovianTest.GasTarget + 1, Spec.JovianTimeStamp, 10_000, 10_000 + 1)]
    [TestCase(2_000_000_000, JovianTest.GasTarget, JovianTest.GasTarget + 10_000_000, Spec.JovianTimeStamp, 1_000_000_000, 2_040_000_000)]
    public void CalculatesBaseFee_AfterJovian_Using(
        long baseFee, ulong gasUsed, ulong blobGasUsed, ulong timestamp,
        long minBaseFee, long expectedBaseFee
    )
    {
        OptimismReleaseSpec releaseSpec = new()
        {
            IsEip1559Enabled = true,
            IsOpHoloceneEnabled = timestamp >= Spec.HoloceneTimeStamp,
            IsOpJovianEnabled = timestamp >= Spec.JovianTimeStamp,
            BaseFeeCalculator = new OptimismBaseFeeCalculator(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp, new DefaultBaseFeeCalculator())
        };

        EIP1559Parameters parameters = releaseSpec.IsOpJovianEnabled
            ? new(1, JovianTest.Denominator, JovianTest.Elasticity, (ulong)minBaseFee)
            : new(0, JovianTest.Denominator, JovianTest.Elasticity);

        byte[] extraData = new byte[parameters.ByteLength];
        parameters.WriteTo(extraData);

        BlockHeader blockHeader = Build.A.BlockHeader
            .WithGasLimit(JovianTest.GasLimit)
            .WithGasUsed(gasUsed)
            .WithBlobGasUsed((ulong)blobGasUsed)
            .WithBaseFee((UInt256)baseFee)
            .WithTimestamp(timestamp)
            .WithExtraData(extraData)
            .TestObject;

        UInt256 actualBaseFee = BaseFeeCalculator.CalculateForReferenceSlot(blockHeader, releaseSpec);

        Assert.That(actualBaseFee, Is.EqualTo((UInt256)expectedBaseFee));
    }

    [Test]
    public void CalculatesBaseFee_AfterJovian_LeavesTheSharedParentHeaderUntouched()
    {
        const ulong gasUsed = JovianTest.GasTarget;
        EIP1559Parameters parameters = new(1, JovianTest.Denominator, JovianTest.Elasticity, 10_000);
        byte[] extraData = new byte[parameters.ByteLength];
        parameters.WriteTo(extraData);
        BlockHeader parent = Build.A.BlockHeader
            .WithGasLimit(JovianTest.GasLimit)
            .WithGasUsed(gasUsed)
            .WithBlobGasUsed(JovianTest.GasTarget + 10_000_000)
            .WithBaseFee(2_000_000_000)
            .WithTimestamp(Spec.JovianTimeStamp)
            .WithExtraData(extraData)
            .TestObject;

        // Another caller sharing the cached parent header must never observe the DA-footprint override, not even mid-calculation.
        ulong? parentGasUsedDuringCalculation = null;
        IBaseFeeCalculator inner = Substitute.For<IBaseFeeCalculator>();
        inner.Calculate(Arg.Any<BlockHeader>(), Arg.Any<ulong>(), Arg.Any<ulong?>(), Arg.Any<IEip1559Spec>()).Returns(_ =>
        {
            parentGasUsedDuringCalculation = parent.GasUsed;
            return UInt256.One;
        });
        OptimismBaseFeeCalculator calculator = new(Spec.HoloceneTimeStamp, Spec.JovianTimeStamp, inner);

        calculator.Calculate(parent, parent.Timestamp + 2, null, new OptimismReleaseSpec { IsEip1559Enabled = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parentGasUsedDuringCalculation, Is.EqualTo(gasUsed));
            Assert.That(parent.GasUsed, Is.EqualTo(gasUsed));
        }
    }
}

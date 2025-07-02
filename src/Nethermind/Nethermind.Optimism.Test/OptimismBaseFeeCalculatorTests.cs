// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismBaseFeeCalculatorTests
{
    /// <remarks>
    /// Tests sourced from <see href="https://github.com/ethereum-optimism/op-geth/blob/1e60ba82d31bc17111481998100cd948ee06c0ab/consensus/misc/eip1559/eip1559_test.go#L191"/>
    /// </remarks>
    [TestCase(15_000_000, 10_000_000, 10u, 2u)] // Target
    [TestCase(10_000_000, 9_666_667, 10u, 2u)] // Below
    [TestCase(20_000_000, 10_333_333, 10u, 2u)] // Above
    [TestCase(3_000_000, 10_000_000, 2u, 10u)] // Target
    [TestCase(1_000_000, 6_666_667, 2u, 10u)] // Below
    [TestCase(30_000_000, 55_000_000, 2u, 10u)] // Above
    public void CalculatesBaseFee_AfterHolocene_UsingExtraDataParameters(long gasUsed, long expectedBaseFee, UInt32 denominator, UInt32 elasticity)
    {
        const ulong HoloceneTimestamp = 10_000_000;

        IReleaseSpec releaseSpec = new ReleaseSpec
        {
            IsEip1559Enabled = true,
            IsOpHoloceneEnabled = true,
            BaseFeeCalculator = new OptimismBaseFeeCalculator(HoloceneTimestamp, new DefaultBaseFeeCalculator())
        };

        var extraData = new byte[EIP1559Parameters.ByteLength];
        var parameters = new EIP1559Parameters(0, denominator, elasticity);
        parameters.WriteTo(extraData);

        BlockHeader blockHeader = Build.A.BlockHeader
            .WithGasLimit(30_000_000)
            .WithBaseFee(10_000_000)
            .WithTimestamp(HoloceneTimestamp)
            .WithGasUsed(gasUsed)
            .WithExtraData(extraData)
            .TestObject;

        UInt256 actualBaseFee = BaseFeeCalculator.Calculate(blockHeader, releaseSpec);

        actualBaseFee.Should().Be((UInt256)expectedBaseFee);
    }
}

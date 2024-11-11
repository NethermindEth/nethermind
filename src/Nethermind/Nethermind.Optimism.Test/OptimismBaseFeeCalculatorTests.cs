// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using NSubstitute;
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
        IReleaseSpec releaseSpec = new ReleaseSpec
        {
            IsEip1559Enabled = true,
            IsOpHoloceneEnabled = true
        };

        var extraData = new byte[32];
        var parameters = new EIP1559Parameters(0, denominator, elasticity);
        parameters.WriteTo(extraData);

        BlockHeader blockHeader = Build.A.BlockHeader
            .WithGasLimit(30_000_000)
            .WithBaseFee(10_000_000)
            .WithGasUsed(gasUsed)
            .WithExtraData(extraData)
            .TestObject;

        var specProvider = Substitute.For<ISpecProvider>();
        specProvider.GetSpec(blockHeader).Returns(releaseSpec);
        var calculator = new OptimismBaseFeeCalculator(new BaseFeeCalculator(), specProvider);

        UInt256 actualBaseFee = calculator.Calculate(blockHeader, releaseSpec);

        actualBaseFee.Should().Be((UInt256)expectedBaseFee);
    }
}

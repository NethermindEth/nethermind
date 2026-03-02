// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcGasLimitCalculatorTests
{
    [Test]
    public void GetGasLimit_WhenDynamicGasLimitNotEnabled_ReturnsTargetBlockGasLimit()
    {
        // Arrange
        const long targetGasLimit = 10_000_000L;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1000);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(parentHeader);

        // Assert
        result.Should().Be(targetGasLimit);
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitNotEnabledAndTargetBlockGasLimitIsNull_ReturnsDefaultXdcGasLimit()
    {
        // Arrange
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns((long?)null);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1000);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(parentHeader);

        // Assert
        result.Should().Be(XdcConstants.DefaultTargetGasLimit);
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitEnabled_UsesTargetAdjustedCalculator()
    {
        // Arrange
        const long parentGasLimit = 84_000_000L;
        const long targetGasLimit = 100_000_000L;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1000, parentGasLimit);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(parentHeader);

        // Assert
        // The TargetAdjustedGasLimitCalculator will adjust the gas limit toward the target
        result.Should().NotBe(parentGasLimit);
        // It should move toward the target (100M) from parent (84M)
        result.Should().BeGreaterThan(parentGasLimit);
        result.Should().BeLessThanOrEqualTo(targetGasLimit);
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitEnabled_GasLimitAdjustsTowardTarget()
    {
        // Arrange
        const long parentGasLimit = 50_000_000L;
        const long targetGasLimit = 84_000_000L;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1000, parentGasLimit);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(parentHeader);

        // Assert
        // Should increase toward target
        result.Should().BeGreaterThan(parentGasLimit);
        result.Should().BeLessThanOrEqualTo(targetGasLimit);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetGasLimit_AtDynamicGasLimitBlockBoundary_TransitionsToTargetAdjusted(bool dynamicBlockActive)
    {
        // Arrange
        const long targetGasLimit = 100_000_000L;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1);
        var spec = CreateXdcSpec(isDynamicGasLimitBlock: dynamicBlockActive);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        long result = calculator.GetGasLimit(parentHeader);

        if (dynamicBlockActive)
        {
            result.Should().BeInRange(parentHeader.GasLimit - 100000, parentHeader.GasLimit + 100000);
        }
        else
        {
            result.Should().Be(targetGasLimit);
        }
    }

    [Test]
    public void GetGasLimit_WhenParentGasLimitEqualsTarget_DynamicModeReturnsNearTarget()
    {
        // Arrange
        const long targetGasLimit = 84_000_000L;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var parentHeader = CreateParentHeader(1000, targetGasLimit);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(parentHeader);

        // Assert
        // When at target, should return close to target
        result.Should().BeGreaterOrEqualTo(targetGasLimit - 100_000);
        result.Should().BeLessThanOrEqualTo(targetGasLimit + 100_000);
    }

    [Test]
    public void GetGasLimit_WithGenesisBlock_HandlesCorrectly()
    {
        // Arrange
        const long targetGasLimit = 84_000_000L;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        var calculator = new XdcGasLimitCalculator(specProvider, blocksConfig);
        var genesisHeader = CreateParentHeader(0, 5_000_000L);
        var xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        long result = calculator.GetGasLimit(genesisHeader);

        // Assert
        result.Should().Be(targetGasLimit);
    }

    private static BlockHeader CreateParentHeader(long number, long gasLimit = 84_000_000L)
    {
        return Build.A.BlockHeader
            .WithNumber(number)
            .WithGasLimit(gasLimit)
            .WithHash(TestItem.KeccakA)
            .TestObject;
    }

    private static IXdcReleaseSpec CreateXdcSpec(bool isDynamicGasLimitBlock)
    {
        var spec = Substitute.For<IXdcReleaseSpec>();
        spec.IsDynamicGasLimitBlock.Returns(isDynamicGasLimitBlock);
        spec.Eip1559TransitionBlock.Returns(long.MaxValue); // Not relevant for these tests
        spec.IsEip1559Enabled.Returns(false);
        spec.GasLimitBoundDivisor.Returns(1024);
        return spec;
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Xdc.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcGasLimitCalculatorTests
{
    [Test]
    public void GetGasLimit_WhenDynamicGasLimitNotEnabled_ReturnsTargetBlockGasLimit()
    {
        // Arrange
        const ulong targetGasLimit = 10_000_000UL;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1000UL);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(parentHeader);

        // Assert
        Assert.That(result, Is.EqualTo(targetGasLimit));
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitNotEnabledAndTargetBlockGasLimitIsNull_ReturnsDefaultXdcGasLimit()
    {
        // Arrange
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns((ulong?)null);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1000UL);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(parentHeader);

        // Assert
        Assert.That(result, Is.EqualTo(XdcConstants.DefaultTargetGasLimit));
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitEnabled_UsesTargetAdjustedCalculator()
    {
        // Arrange
        const ulong parentGasLimit = 84_000_000UL;
        const ulong targetGasLimit = 100_000_000UL;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1000UL, parentGasLimit);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(parentHeader);

        // Assert
        // The TargetAdjustedGasLimitCalculator will adjust the gas limit toward the target
        Assert.That(result, Is.Not.EqualTo(parentGasLimit));
        // It should move toward the target (100M) from parent (84M)
        Assert.That(result, Is.GreaterThan(parentGasLimit));
        Assert.That(result, Is.LessThanOrEqualTo(targetGasLimit));
    }

    [Test]
    public void GetGasLimit_WhenDynamicGasLimitEnabled_GasLimitAdjustsTowardTarget()
    {
        // Arrange
        const ulong parentGasLimit = 50_000_000UL;
        const ulong targetGasLimit = 84_000_000UL;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1000UL, parentGasLimit);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(parentHeader);

        // Assert
        // Should increase toward target
        Assert.That(result, Is.GreaterThan(parentGasLimit));
        Assert.That(result, Is.LessThanOrEqualTo(targetGasLimit));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetGasLimit_AtDynamicGasLimitBlockBoundary_TransitionsToTargetAdjusted(bool dynamicBlockActive)
    {
        // Arrange
        const ulong targetGasLimit = 100_000_000UL;
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1UL);
        IXdcReleaseSpec spec = CreateXdcSpec(isDynamicGasLimitBlock: dynamicBlockActive);
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        ulong result = calculator.GetGasLimit(parentHeader);

        if (dynamicBlockActive)
        {
            Assert.That(result, Is.InRange(parentHeader.GasLimit - 100000UL, parentHeader.GasLimit + 100000UL));
        }
        else
        {
            Assert.That(result, Is.EqualTo(targetGasLimit));
        }
    }

    [Test]
    public void GetGasLimit_WhenParentGasLimitEqualsTarget_DynamicModeReturnsNearTarget()
    {
        // Arrange
        const ulong targetGasLimit = 84_000_000UL;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader parentHeader = CreateParentHeader(1000UL, targetGasLimit);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: true);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(parentHeader);

        // Assert
        // When at target, should return close to target
        Assert.That(result, Is.GreaterThanOrEqualTo(targetGasLimit - 100_000UL));
        Assert.That(result, Is.LessThanOrEqualTo(targetGasLimit + 100_000UL));
    }

    [Test]
    public void GetGasLimit_WithGenesisBlock_HandlesCorrectly()
    {
        // Arrange
        const ulong targetGasLimit = 84_000_000UL;

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        IBlocksConfig blocksConfig = Substitute.For<IBlocksConfig>();
        blocksConfig.TargetBlockGasLimit.Returns(targetGasLimit);

        XdcGasLimitCalculator calculator = new(specProvider, blocksConfig);
        BlockHeader genesisHeader = CreateParentHeader(0UL, 5_000_000UL);
        IXdcReleaseSpec xdcSpec = CreateXdcSpec(isDynamicGasLimitBlock: false);

        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(xdcSpec);

        // Act
        ulong result = calculator.GetGasLimit(genesisHeader);

        // Assert
        Assert.That(result, Is.EqualTo(targetGasLimit));
    }

    private static BlockHeader CreateParentHeader(ulong number, ulong gasLimit = 84_000_000UL) =>
        Build.A.BlockHeader
            .WithNumber(number)
            .WithGasLimit(gasLimit)
            .WithHash(TestItem.KeccakA)
            .TestObject;

    private static IXdcReleaseSpec CreateXdcSpec(bool isDynamicGasLimitBlock)
    {
        IXdcReleaseSpec spec = Substitute.For<IXdcReleaseSpec>();
        spec.IsDynamicGasLimitBlock.Returns(isDynamicGasLimitBlock);
        spec.Eip1559TransitionBlock.Returns(ulong.MaxValue); // Not relevant for these tests
        spec.IsEip1559Enabled.Returns(false);
        spec.GasLimitBoundDivisor.Returns(1024UL);
        return spec;
    }
}

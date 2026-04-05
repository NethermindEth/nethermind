// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.EraE.Proofs;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Proofs;

public class BlocksRootContextTests
{
    [Test]
    public void Constructor_WithBlockNumberBelowParis_SetsHistoricalHashesAccumulatorType()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);

        sut.AccumulatorType.Should().Be(AccumulatorType.HistoricalHashesAccumulator);
    }

    [Test]
    public void Constructor_WithParisBlockNumberAndPreShanghaiTimestamp_SetsHistoricalRootsType()
    {
        MainnetSpecProvider specProvider = new();
        specProvider.UpdateMergeTransitionInfo(MainnetSpecProvider.ParisBlockNumber + 1);
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, 1_600_000_000UL, specProvider);

        sut.AccumulatorType.Should().Be(AccumulatorType.HistoricalRoots);
    }

    [Test]
    public void Constructor_WithShanghaiBlockTimestamp_SetsHistoricalSummariesType()
    {
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);

        sut.AccumulatorType.Should().Be(AccumulatorType.HistoricalSummaries);
    }

    [Test]
    public void ProcessBlock_InPreMergeContext_SetsPopulated()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);
        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(1L).TestObject;

        sut.ProcessBlock(block);

        sut.Populated.Should().BeTrue();
    }

    [Test]
    public void AccumulatorRoot_WhenFinalizeContextNeverCalled_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);

        sut.Invoking(c => _ = c.AccumulatorRoot).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AccumulatorRoot_WhenFinalizeContextCalledWithoutBlocks_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);

        sut.FinalizeContext();

        sut.Invoking(c => _ = c.AccumulatorRoot).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void HistoricalRoot_WhenNotFinalized_ThrowsInvalidOperationException()
    {
        MainnetSpecProvider specProvider = new();
        specProvider.UpdateMergeTransitionInfo(MainnetSpecProvider.ParisBlockNumber + 1);
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, 1_600_000_000UL, specProvider);

        sut.Invoking(c => _ = c.HistoricalRoot).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void HistoricalSummary_WhenNotFinalized_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);

        sut.Invoking(c => _ = c.HistoricalSummary).Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void FinalizeContext_InPreMergeContext_SetsAccumulatorRoot()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);
        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(1L).TestObject;
        sut.ProcessBlock(block);

        sut.FinalizeContext();

        sut.Invoking(c => _ = c.AccumulatorRoot).Should().NotThrow();
    }

    [Test]
    public void FinalizeContext_InHistoricalRootsContext_SetsHistoricalRoot()
    {
        MainnetSpecProvider specProvider = new();
        specProvider.UpdateMergeTransitionInfo(MainnetSpecProvider.ParisBlockNumber + 1);
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, 1_600_000_000UL, specProvider);
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ParisBlockNumber + 1).TestObject;
        sut.ProcessBlock(block,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"),
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));

        sut.FinalizeContext();

        sut.Invoking(c => _ = c.HistoricalRoot).Should().NotThrow();
    }

    [Test]
    public void FinalizeContext_InHistoricalSummariesContext_SetsHistoricalSummary()
    {
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ParisBlockNumber + 1).TestObject;
        sut.ProcessBlock(block,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"),
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));

        sut.FinalizeContext();

        sut.Invoking(c => _ = c.HistoricalSummary).Should().NotThrow();
    }
}

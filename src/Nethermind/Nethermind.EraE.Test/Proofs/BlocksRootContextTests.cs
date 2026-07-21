// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        Assert.That(sut.AccumulatorType, Is.EqualTo(AccumulatorType.HistoricalHashesAccumulator));
    }

    [Test]
    public void Constructor_WithParisBlockNumberAndPreShanghaiTimestamp_SetsHistoricalRootsType()
    {
        using BlocksRootContext sut = CreateHistoricalRootsContext();

        Assert.That(sut.AccumulatorType, Is.EqualTo(AccumulatorType.HistoricalRoots));
    }

    [Test]
    public void Constructor_WithShanghaiBlockTimestamp_SetsHistoricalSummariesType()
    {
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);

        Assert.That(sut.AccumulatorType, Is.EqualTo(AccumulatorType.HistoricalSummaries));
    }

    [Test]
    public void ProcessBlock_InPreMergeContext_SetsPopulated()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);
        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(1L).TestObject;

        sut.ProcessBlock(block);

        Assert.That(sut.Populated, Is.True);
    }

    [Test]
    public void AccumulatorRoot_WhenFinalizeContextNeverCalled_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);

        Assert.That(() => _ = sut.AccumulatorRoot, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void AccumulatorRoot_WhenFinalizeContextCalledWithoutBlocks_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);

        sut.FinalizeContext();

        Assert.That(() => _ = sut.AccumulatorRoot, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void HistoricalRoot_WhenNotFinalized_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = CreateHistoricalRootsContext();

        Assert.That(() => _ = sut.HistoricalRoot, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void HistoricalSummary_WhenNotFinalized_ThrowsInvalidOperationException()
    {
        using BlocksRootContext sut = new(MainnetSpecProvider.ParisBlockNumber + 1, MainnetSpecProvider.ShanghaiBlockTimestamp, MainnetSpecProvider.Instance);

        Assert.That(() => _ = sut.HistoricalSummary, Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public void FinalizeContext_InPreMergeContext_SetsAccumulatorRoot()
    {
        using BlocksRootContext sut = new(0, specProvider: MainnetSpecProvider.Instance);
        Block block = Build.A.Block.WithNumber(0).WithTotalDifficulty(1L).TestObject;
        sut.ProcessBlock(block);

        sut.FinalizeContext();

        Assert.That(() => _ = sut.AccumulatorRoot, Throws.Nothing);
    }

    [Test]
    public void FinalizeContext_InHistoricalRootsContext_SetsHistoricalRoot()
    {
        using BlocksRootContext sut = CreateHistoricalRootsContext();
        Block block = Build.A.Block.WithNumber(MainnetSpecProvider.ParisBlockNumber + 1).TestObject;
        sut.ProcessBlock(block,
            new ValueHash256("0xaabbccdd00000000000000000000000000000000000000000000000000000000"),
            new ValueHash256("0x1122334400000000000000000000000000000000000000000000000000000000"));

        sut.FinalizeContext();

        Assert.That(() => _ = sut.HistoricalRoot, Throws.Nothing);
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

        Assert.That(() => _ = sut.HistoricalSummary, Throws.Nothing);
    }

    private static BlocksRootContext CreateHistoricalRootsContext()
    {
        MainnetSpecProvider specProvider = new();
        specProvider.UpdateMergeTransitionInfo(MainnetSpecProvider.ParisBlockNumber + 1);
        return new BlocksRootContext(MainnetSpecProvider.ParisBlockNumber + 1, 1_600_000_000UL, specProvider);
    }
}

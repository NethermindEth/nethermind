// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EraE.Archive;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using EraException = Nethermind.Era1.EraException;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

internal class EraReaderTests
{
    [TestCase(3, 0)]
    [TestCase(0, 3)]
    public async Task GetBlockByNumber_ReturnsCorrectBlockNumbers(int preMergeCount, int postMergeCount)
    {
        int totalCount = preMergeCount + postMergeCount;
        using TestEraFile file = await TestEraFile.Create(preMergeCount: preMergeCount, postMergeCount: postMergeCount);
        using EraReader sut = new(file.FilePath);

        for (int i = 0; i < totalCount; i++)
        {
            (Block block, _) = await sut.GetBlockByNumber(i);
            block.Number.Should().Be(i);
        }
    }

    [Test]
    public async Task GetBlockByNumber_InPreMergeEpoch_TotalDifficultyIsRestored()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        (Block block, _) = await sut.GetBlockByNumber(2);
        block.TotalDifficulty.Should().Be(file.Contents[2].Block.TotalDifficulty);
    }

    [Test]
    public async Task ReadAccumulatorRoot_InPreMergeEpoch_Succeeds()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        Assert.That(() => sut.ReadAccumulatorRoot(), Throws.Nothing);
    }

    [Test]
    public async Task VerifyContent_PreMergeEpoch_AccumulatorMatchesComputed()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);

        using AccumulatorCalculator calculator = new();
        foreach ((Block block, _) in file.Contents)
            calculator.Add(block.Hash!, block.TotalDifficulty!.Value);

        ValueHash256 expectedRoot = calculator.ComputeRoot();

        using EraReader sut = new(file.FilePath);
        ValueHash256 verifiedRoot = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);

        verifiedRoot.Should().Be(expectedRoot);
    }

    [Test]
    public async Task GetAsyncEnumerator_InPreMergeEpoch_YieldsAllBlocks()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        List<(Block, TxReceipt[])> result = await sut.ToListAsync();
        result.Should().HaveCount(3);
        result.Select(r => r.Item1.Number).Should().BeEquivalentTo([0L, 1L, 2L]);
    }

    [Test]
    public async Task ReadAccumulatorRoot_InPostMergeEpoch_ThrowsEraException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        using EraReader sut = new(file.FilePath);

        Assert.That(() => sut.ReadAccumulatorRoot(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task VerifyContent_InPostMergeEpoch_ReturnsDefaultHash()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        using EraReader sut = new(file.FilePath);

        ValueHash256 result = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);
        result.Should().Be(default, "post-merge epochs have no accumulator");
    }

    [Test]
    public async Task VerifyContent_InTransitionEpoch_AccumulatorCoversOnlyPreMergeBlocks()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 2);

        using AccumulatorCalculator calculator = new();
        foreach ((Block block, _) in file.Contents.Where(c => !c.Block.Header.IsPostMerge))
            calculator.Add(block.Hash!, block.TotalDifficulty!.Value);

        ValueHash256 expectedRoot = calculator.ComputeRoot();

        using EraReader sut = new(file.FilePath);
        ValueHash256 verifiedRoot = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);

        verifiedRoot.Should().Be(expectedRoot,
            "transition epoch accumulator must only cover pre-merge blocks");
    }

    [Test]
    public async Task GetBlockByNumber_InTransitionEpoch_FirstPostMergeBlockHasIsPostMergeTrue()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 2);
        using EraReader sut = new(file.FilePath);

        (Block postMergeBlock, _) = await sut.GetBlockByNumber(2);
        postMergeBlock.Header.IsPostMerge.Should().BeTrue();
    }

    [Test]
    public async Task GetBlockByNumber_WithBelowRangeNumber_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GetBlockByNumber_WithAboveRangeNumber_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(999), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GetBlockByNumber_WithSlimEncodedReceipts_BloomIsReconstructedFromLogs()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 1, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        (_, TxReceipt[] receipts) = await sut.GetBlockByNumber(0);

        receipts.Should().NotBeEmpty();
        receipts[0].Bloom.Should().NotBeNull("bloom must be auto-computed from logs");
    }

}

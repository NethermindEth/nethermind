// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.EraE.Archive;
using AccumulatorCalculator = Nethermind.Era1.AccumulatorCalculator;
using EraException = Nethermind.Era1.Exceptions.EraException;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Archive;

internal class EraReaderTests
{
    [TestCase(3U, 0U)]
    [TestCase(0U, 3U)]
    public async Task GetBlockByNumber_ReturnsCorrectBlockNumbers(uint preMergeCount, uint postMergeCount)
    {
        ulong totalCount = preMergeCount + postMergeCount;
        using TestEraFile file = await TestEraFile.Create(preMergeCount: preMergeCount, postMergeCount: postMergeCount);
        using EraReader sut = new(file.FilePath);

        for (ulong i = 0; i < totalCount; i++)
        {
            (Block block, _) = await sut.GetBlockByNumber(i);
            Assert.That(block.Number, Is.EqualTo(i));
        }
    }

    [Test]
    public async Task GetBlockByNumber_InPreMergeEpoch_TotalDifficultyIsRestored()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        (Block block, _) = await sut.GetBlockByNumber(2UL);
        Assert.That(block.TotalDifficulty, Is.EqualTo(file.Contents[2].Block.TotalDifficulty));
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

        Assert.That(verifiedRoot, Is.EqualTo(expectedRoot));
    }

    [Test]
    public async Task GetAsyncEnumerator_InPreMergeEpoch_YieldsAllBlocks()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        List<(Block, TxReceipt[])> result = await sut.ToListAsync();
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.Select(r => r.Item1.Number), Is.EqualTo([0L, 1L, 2L]));
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
        Assert.That(result, Is.EqualTo(default(ValueHash256)), "post-merge epochs have no accumulator");
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

        Assert.That(verifiedRoot, Is.EqualTo(expectedRoot), "transition epoch accumulator must only cover pre-merge blocks");
    }

    [Test]
    public async Task GetBlockByNumber_InTransitionEpoch_FirstPostMergeBlockHasIsPostMergeTrue()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 2);
        using EraReader sut = new(file.FilePath);

        (Block postMergeBlock, _) = await sut.GetBlockByNumber(2UL);
        Assert.That(postMergeBlock.Header.IsPostMerge, Is.True);
    }

    [Test]
    public async Task GetBlockByNumber_WithBelowRangeNumber_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(ulong.MaxValue), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GetBlockByNumber_WithAboveRangeNumber_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(999UL), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GetBlockByNumber_WithSlimEncodedReceipts_BloomIsReconstructedFromLogs()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 1, postMergeCount: 0);
        using EraReader sut = new(file.FilePath);

        (_, TxReceipt[] receipts) = await sut.GetBlockByNumber(0UL);

        Assert.That(receipts, Is.Not.Empty);
        Assert.That(receipts[0].Bloom, Is.Not.Null, "bloom must be auto-computed from logs");
    }

}

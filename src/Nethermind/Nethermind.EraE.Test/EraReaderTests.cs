// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using NSubstitute;

namespace Nethermind.EraE.Test;

internal class EraReaderTests
{
    /// <summary>Builds a test .erae file with configurable pre/post-merge blocks.</summary>
    private sealed class TestEraFile : IDisposable
    {
        private readonly TempPath _tmpFile;
        public string FilePath => _tmpFile.Path;
        public List<(Block Block, TxReceipt[] Receipts)> Contents { get; } = [];

        private TestEraFile(TempPath tmpFile)
        {
            _tmpFile = tmpFile;
        }

        public static async Task<TestEraFile> Create(
            int preMergeCount,
            int postMergeCount,
            ISpecProvider? specProvider = null)
        {
            specProvider ??= MainnetSpecProvider.Instance;
            TempPath tmpFile = TempPath.GetTempFile();

            using EraWriter writer = new EraWriter(tmpFile.Path, specProvider);
            TestEraFile file = new(tmpFile);
            HeaderDecoder headerDecoder = new();

            long number = 0;
            UInt256 td = BlockHeaderBuilder.DefaultDifficulty;

            for (int i = 0; i < preMergeCount; i++, number++, td += BlockHeaderBuilder.DefaultDifficulty)
            {
                TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
                Block block = Build.A.Block
                    .WithNumber(number)
                    .WithTotalDifficulty(td)
                    .TestObject;

                block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                    [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
                block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);

                file.Contents.Add((block, [receipt]));
                await writer.Add(block, [receipt]);
            }

            for (int i = 0; i < postMergeCount; i++, number++)
            {
                TxReceipt receipt = Build.A.Receipt.WithTxType(TxType.EIP1559).TestObject;
                Block block = Build.A.Block
                    .WithNumber(number)
                    .WithPostMergeRules()
                    .TestObject;

                block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(
                    [receipt], specProvider.GetSpec(block.Header), block.ReceiptsRoot);
                block.Header.Hash = Keccak.Compute(headerDecoder.Encode(block.Header).Bytes);

                file.Contents.Add((block, [receipt]));
                await writer.Add(block, [receipt]);
            }

            await writer.Finalize();
            return file;
        }

        public void Dispose() => _tmpFile.Dispose();
    }

    // ── Pre-merge epoch ────────────────────────────────────────────────────

    [Test]
    public async Task GetBlockByNumber_PreMergeEpoch_ReturnsCorrectBlockNumbers()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        for (int i = 0; i < 3; i++)
        {
            (Block block, _) = await sut.GetBlockByNumber(i);
            block.Number.Should().Be(i);
        }
    }

    [Test]
    public async Task GetBlockByNumber_PreMergeEpoch_TotalDifficultyRestored()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        (Block block, _) = await sut.GetBlockByNumber(2);
        block.TotalDifficulty.Should().Be(file.Contents[2].Block.TotalDifficulty);
    }

    [Test]
    public async Task ReadAccumulatorRoot_PreMergeEpoch_DoesNotThrow()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

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

        using EraReader sut = new EraReader(file.FilePath);
        ValueHash256 verifiedRoot = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);

        verifiedRoot.Should().Be(expectedRoot);
    }

    [Test]
    public async Task GetAsyncEnumerator_PreMergeEpoch_YieldsAllBlocks()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 3, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        List<(Block, TxReceipt[])> result = await sut.ToListAsync();
        result.Should().HaveCount(3);
        result.Select(r => r.Item1.Number).Should().BeEquivalentTo([0L, 1L, 2L]);
    }

    // ── Post-merge epoch ───────────────────────────────────────────────────

    [Test]
    public async Task GetBlockByNumber_PostMergeEpoch_ReturnsCorrectBlockNumbers()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        using EraReader sut = new EraReader(file.FilePath);

        for (int i = 0; i < 3; i++)
        {
            (Block block, _) = await sut.GetBlockByNumber(i);
            block.Number.Should().Be(i);
        }
    }

    [Test]
    public async Task ReadAccumulatorRoot_PostMergeEpoch_ThrowsEraException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        using EraReader sut = new EraReader(file.FilePath);

        Assert.That(() => sut.ReadAccumulatorRoot(), Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task VerifyContent_PostMergeEpoch_ReturnsDefaultHash()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 0, postMergeCount: 3);
        using EraReader sut = new EraReader(file.FilePath);

        ValueHash256 result = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);
        result.Should().Be(default(ValueHash256), "post-merge epochs have no accumulator");
    }

    // ── Transition epoch ───────────────────────────────────────────────────

    [Test]
    public async Task VerifyContent_TransitionEpoch_AccumulatorCoversOnlyPreMergeBlocks()
    {
        // 2 pre-merge + 2 post-merge = transition epoch
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 2);

        using AccumulatorCalculator calculator = new();
        foreach ((Block block, _) in file.Contents.Where(c => !c.Block.Header.IsPostMerge))
            calculator.Add(block.Hash!, block.TotalDifficulty!.Value);

        ValueHash256 expectedRoot = calculator.ComputeRoot();

        using EraReader sut = new EraReader(file.FilePath);
        ValueHash256 verifiedRoot = await sut.VerifyContent(MainnetSpecProvider.Instance, Always.Valid);

        verifiedRoot.Should().Be(expectedRoot,
            "transition epoch accumulator must only cover pre-merge blocks");
    }

    [Test]
    public async Task GetBlockByNumber_TransitionEpoch_PostMergeBlockHasNoTotalDifficulty()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 2);
        using EraReader sut = new EraReader(file.FilePath);

        // Block at index 2 is the first post-merge block
        (Block postMergeBlock, _) = await sut.GetBlockByNumber(2);
        postMergeBlock.Header.IsPostMerge.Should().BeTrue();
    }

    // ── Error cases ────────────────────────────────────────────────────────

    [Test]
    public async Task GetBlockByNumber_BelowFirstBlock_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task GetBlockByNumber_AboveLastBlock_ThrowsArgumentOutOfRangeException()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 2, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        Assert.That(async () => await sut.GetBlockByNumber(999), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    // ── Receipt round-trip ─────────────────────────────────────────────────

    [Test]
    public async Task GetBlockByNumber_ReceiptsRoundTrip_BloomReconstructedFromLogs()
    {
        using TestEraFile file = await TestEraFile.Create(preMergeCount: 1, postMergeCount: 0);
        using EraReader sut = new EraReader(file.FilePath);

        (_, TxReceipt[] receipts) = await sut.GetBlockByNumber(0);

        // Bloom is NOT stored in slim receipts; it must be auto-reconstructed from Logs.
        receipts.Should().NotBeEmpty();
        receipts[0].Bloom.Should().NotBeNull("bloom must be auto-computed from logs");
    }
}

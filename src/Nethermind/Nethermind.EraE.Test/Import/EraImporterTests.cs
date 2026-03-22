// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using EraException = Nethermind.Era1.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using NUnit.Framework;

namespace Nethermind.EraE.Test.Import;

public class EraImporterTests
{
    [Test]
    public async Task Import_WithEmptyDirectory_ThrowsEraException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();

        string tmpDirectory = ctx.ResolveTempDirPath();
        System.IO.Directory.CreateDirectory(tmpDirectory);
        await System.IO.File.WriteAllTextAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName), "");

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import(tmpDirectory, 0, 0, null),
            Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Import_WithValidEraFiles_ImportsAllBlocksIntoTree()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        await sut.Import(exportPath, 0, long.MaxValue, null);

        for (long i = 1; i < chainLength; i++)
        {
            targetTree.FindBlock(i, BlockTreeLookupOptions.None).Should().NotBeNull(
                $"block {i} should have been imported");
        }
    }

    [Test]
    public async Task Import_WithTrustedAccumulators_Succeeds()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string accumulatorPath = System.IO.Path.Combine(exportPath, EraExporter.AccumulatorFileName);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, accumulatorPath),
            Throws.Nothing);
    }

    [Test]
    public async Task Import_WithModifiedChecksum_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string checksumPath = System.IO.Path.Combine(exportPath, EraExporter.ChecksumsFileName);
        string[] lines = await System.IO.File.ReadAllLinesAsync(checksumPath);
        lines[^1] = "0x0000000000000000000000000000000000000000000000000000000000000000 " +
                    System.IO.Path.GetFileName(lines[^1].Split(' ')[^1]);
        await System.IO.File.WriteAllLinesAsync(checksumPath, lines);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WithWrongTrustedAccumulator_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        string fakeAccumulatorPath = System.IO.Path.Combine(exportPath, "fake_accumulators.txt");
        string[] accLines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(exportPath, EraExporter.AccumulatorFileName));
        string[] fakeLines = accLines.Select(l =>
            "0x0000000000000000000000000000000000000000000000000000000000000000 " +
            l.Split(' ')[^1]).ToArray();
        await System.IO.File.WriteAllLinesAsync(fakeAccumulatorPath, fakeLines);

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, fakeAccumulatorPath),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WithPartialRange_ImportsOnlyRequestedBlocks()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        await sut.Import(exportPath, 0, 15, null);

        for (long i = 1; i <= 15; i++)
            targetTree.FindBlock(i, BlockTreeLookupOptions.None).Should().NotBeNull($"block {i} should have been imported");

        targetTree.FindBlock(16, BlockTreeLookupOptions.None).Should().BeNull("block 16 is outside the requested range");
    }

    [Test]
    public async Task Import_WhenCalledTwice_DoesNotThrowAndIsIdempotent()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        await sut.Import(exportPath, 0, long.MaxValue, null);

        Assert.That(() => sut.Import(exportPath, 0, long.MaxValue, null), Throws.Nothing,
            "re-importing the same range must be idempotent");
    }

    [Test]
    public async Task ExportThenImport_RoundTrip_BlocksAndReceiptsMatchOriginal()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        IReceiptStorage sourceReceipts = sourceCtx.Resolve<IReceiptStorage>();

        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<ISyncConfig>(new SyncConfig { FastSync = true })
            .Build();

        await targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, long.MaxValue, null);

        IReceiptStorage targetReceipts = targetCtx.Resolve<IReceiptStorage>();

        for (long i = 1; i < chainLength; i++)
        {
            Block? original = sourceTree.FindBlock(i, BlockTreeLookupOptions.None);
            Block? imported = targetTree.FindBlock(i, BlockTreeLookupOptions.None);

            imported.Should().NotBeNull($"block {i} should exist after import");
            imported!.Hash.Should().Be(original!.Hash!, $"block {i} hash must match");

            TxReceipt[] originalReceipts = sourceReceipts.Get(original!);
            bool hasReceipts = targetReceipts.HasBlock(imported.Number, imported.Hash!);
            if (originalReceipts.Length > 0)
                hasReceipts.Should().BeTrue($"receipts for block {i} should have been imported");
        }
    }

    [Test]
    public async Task ExportThenImport_PostMergeRoundTrip_BlocksAndReceiptsMatchOriginal()
    {
        const int chainLength = 16;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<ISyncConfig>(new SyncConfig { FastSync = true })
            .Build();

        await targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, long.MaxValue, null);

        IReceiptStorage sourceReceipts = sourceCtx.Resolve<IReceiptStorage>();
        IReceiptStorage targetReceipts = targetCtx.Resolve<IReceiptStorage>();

        for (long i = 1; i < chainLength; i++)
        {
            Block? original = sourceTree.FindBlock(i, BlockTreeLookupOptions.None);
            Block? imported = targetTree.FindBlock(i, BlockTreeLookupOptions.None);

            imported.Should().NotBeNull($"post-merge block {i} should exist after import");
            imported!.Hash.Should().Be(original!.Hash!, $"post-merge block {i} hash must match");

            TxReceipt[] originalReceipts = sourceReceipts.Get(original!);
            bool hasReceipts = targetReceipts.HasBlock(imported.Number, imported.Hash!);
            if (originalReceipts.Length > 0)
                hasReceipts.Should().BeTrue($"receipts for post-merge block {i} should have been imported");
        }
    }

    [Test]
    public async Task Import_WhenBlocksPrePopulatedWithoutTotalDifficulty_SetsCorrectTotalDifficulty()
    {
        // Simulate the snap sync ancient-bodies phase: block bodies exist in the tree but were
        // inserted without TotalDifficulty (blockInfo.TD=0). Era import must re-insert the header
        // with the correct TD — either from the era file or computed via SetTotalDifficulty.
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        for (long i = 1; i < chainLength; i++)
        {
            Block block = sourceTree.FindBlock(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
            targetTree.Insert(block,
                BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks,
                BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        }

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        await targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, long.MaxValue, null);

        for (long i = 1; i < chainLength; i++)
        {
            Block? imported = targetTree.FindBlock(i, BlockTreeLookupOptions.None);
            imported.Should().NotBeNull($"block {i} should exist");
            imported!.TotalDifficulty.Should().NotBeNull($"block {i} should have TotalDifficulty after import");
        }
    }

    [Test]
    public async Task Import_WhenBlockFailsValidation_ThrowsEraVerificationException()
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(32, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<IBlockValidator>(Always.Invalid)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();

        Assert.That(
            () => sut.Import(exportPath, 0, long.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }
}

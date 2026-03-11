// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.EraE.Test;

public class EraImporterTests
{
    [Test]
    public async Task Import_DirectoryDoesNotExist_ThrowsArgumentException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import("/nonexistent/path", 0, 0, null),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Import_EmptyDirectory_ThrowsEraException()
    {
        using IContainer ctx = EraETestModule.BuildContainerBuilder().Build();

        string tmpDirectory = ctx.ResolveTempDirPath();
        System.IO.Directory.CreateDirectory(tmpDirectory);
        // Write empty checksums file so EraStore can be created
        await System.IO.File.WriteAllTextAsync(
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsFileName), "");

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import(tmpDirectory, 0, 0, null),
            Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Import_FullRoundTrip_BlocksImportedIntoBlockTree()
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

        // All blocks should have been inserted
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

        // Corrupt the last checksum entry
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

        // Write a fake accumulators file with zero hashes
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
    public async Task Import_BlockFailsValidation_ThrowsEraVerificationException()
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

    [CancelAfter(4000)]
    [Retry(3)]
    [Test]
    public async Task Import_WillPaceBlockSuggestion(CancellationToken token)
    {
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(64, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();

        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<IEraEConfig>(new EraEConfig
            {
                ImportBlocksBufferSize = 10,
                MaxEraSize = 16,
                NetworkName = EraETestModule.TestNetwork
            })
            .Build();

        ManualResetEventSlim reachedBlock = new();
        bool shouldUpdateMainChain = false;
        long maxSuggestedBlock = 0;
        const long expectedStopBlock = 10;

        targetTree.NewBestSuggestedBlock += (_, args) =>
        {
            if (shouldUpdateMainChain) targetTree.UpdateMainChain([args.Block], true);
            maxSuggestedBlock = args.Block.Number;
            if (args.Block.Number == expectedStopBlock) reachedBlock.Set();
        };

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        Task importTask = sut.Import(exportPath, 0, long.MaxValue, null, token);

        reachedBlock.Wait(token);
        await Task.Delay(100, token);

        maxSuggestedBlock.Should().Be(expectedStopBlock, "pacer should limit suggestion buffer");

        shouldUpdateMainChain = true;
        targetTree.UpdateMainChain(
            [targetTree.FindBlock(expectedStopBlock, BlockTreeLookupOptions.None)!], true);

        await importTask;
    }
}

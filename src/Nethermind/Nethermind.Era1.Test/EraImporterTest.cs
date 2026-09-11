// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Test.Builders;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Era1.Exceptions;

namespace Nethermind.Era1.Test;

public class EraImporterTest
{
    [Test]
    public void ImportAsArchiveSync_DirectoryContainsNoEraFiles_ThrowEraImportException()
    {
        using IContainer testContext = EraTestModule.BuildContainerBuilder().Build();

        string tmpDirectory = testContext.ResolveTempDirPath();
        IEraImporter sut = testContext.Resolve<IEraImporter>();

        Assert.That(() => sut.Import(tmpDirectory, 0, 0, null, CancellationToken.None), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task ImportAsArchiveSync_DirectoryContainsWrongEraFiles_ThrowEraImportException()
    {
        await using IContainer testContext = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();
        string tempDirectory = testContext.ResolveTempDirPath();

        IFileSystem fileSystem = testContext.Resolve<IFileSystem>();
        fileSystem.Directory.CreateDirectory(tempDirectory);

        await fileSystem.File.WriteAllBytesAsync(Path.Join(tempDirectory, EraExporter.ChecksumsFileName), []);

        string badFilePath = Path.Join(tempDirectory, "abc-00000-00000000.era1");
        FileSystemStream stream = fileSystem.File.Create(badFilePath);
        await stream.WriteAsync("\0\0"u8.ToArray());
        stream.Close();

        IEraImporter sut = testContext.Resolve<IEraImporter>();
        Assert.That(() => sut.Import(tempDirectory, 0, 0, null, CancellationToken.None), Throws.TypeOf<EraFormatException>());
    }

    [Test]
    public async Task ImportAsArchiveSync_BlockCannotBeValidated_ThrowEraImportException()
    {
        await using IContainer testCtx = await EraTestModule.CreateExportedEraEnv(512);
        IBlockTree sourceBlocktree = testCtx.Resolve<IBlockTree>();

        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        blockTree.SuggestBlock(sourceBlocktree.FindBlock(0)!, BlockTreeSuggestOptions.None);

        await using IContainer targetCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<IBlockValidator>(Always.Invalid)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        Assert.That(() => sut.Import(testCtx.ResolveTempDirPath(), 0, 0, null, CancellationToken.None), Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithExpected_DoesNotThrow()
    {
        const int ChainLength = 128;
        await using IContainer fromCtx = await EraTestModule.CreateExportedEraEnv(ChainLength);

        string destinationPath = fromCtx.ResolveTempDirPath();

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(fromCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;

        await using IContainer toCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();

        IEraImporter sut = toCtx.Resolve<IEraImporter>();
        await sut.Import(destinationPath, 0, ulong.MaxValue, Path.Join(destinationPath, EraExporter.AccumulatorFileName), default);
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithUnexpected_ThrowEraVerificationException()
    {
        await using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        IFileSystem fileSystem = outputCtx.Resolve<IFileSystem>();
        string destinationPath = outputCtx.ResolveTempDirPath();

        string accumulatorPath = Path.Combine(destinationPath, EraExporter.AccumulatorFileName);
        ValueHash256[] accumulators = (await outputCtx.Resolve<IFileSystem>().File.ReadAllLinesAsync(accumulatorPath))
            .Select(EraPathUtils.ExtractHashFromAccumulatorAndCheckSumEntry).ToArray();

        accumulators[^1] = default;
        await fileSystem.File.WriteAllLinesAsync(accumulatorPath, accumulators.Select(acc => acc.ToString(false)));

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;
        await using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Func<Task> importTask = () => sut.Import(destinationPath, 0, ulong.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), CancellationToken.None);

        Assert.That(importTask, Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task VerifyEraFiles_ModifiedChecksum_ThrowEraVerificationException()
    {
        await using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        IFileSystem fileSystem = outputCtx.Resolve<IFileSystem>();
        string destinationPath = outputCtx.ResolveTempDirPath();

        string checksumPath = Path.Combine(destinationPath, EraExporter.ChecksumsFileName);
        ValueHash256[] checksums = (await outputCtx.Resolve<IFileSystem>().File.ReadAllLinesAsync(checksumPath))
            .Select(EraPathUtils.ExtractHashFromAccumulatorAndCheckSumEntry).ToArray();
        checksums[^1] = default;
        await fileSystem.File.WriteAllLinesAsync(checksumPath, checksums.Select(acc => acc.ToString(false)));

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;
        await using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Func<Task> importTask = () => sut.Import(destinationPath, 0, ulong.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), CancellationToken.None);

        Assert.That(importTask, Throws.TypeOf<EraVerificationException>());
    }

    [CancelAfter(30_000)]
    [Test]
    public async Task ImportAsArchiveSync_WillPaceSuggestBlock(CancellationToken token)
    {
        await using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        string destinationPath = outputCtx.ResolveTempDirPath();

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                ImportBlocksBufferSize = 10,
                MaxEra1Size = 16,
                NetworkName = EraTestModule.TestNetwork
            })
            .Build();

        bool shouldAdvanceMainChain = false;
        ulong maxSuggestedBlocks = 0;
        ulong expectedStopBlock = 10;
        inTree.NewBestSuggestedBlock += (sender, args) =>
        {
            if (shouldAdvanceMainChain) inTree.TryUpdateMainChain(args.Block.Header, true, preloadedBlocks: new[] { args.Block });
            maxSuggestedBlocks = args.Block.Number;
        };

        EraImporter sut = (EraImporter)inCtx.Resolve<IEraImporter>();
        Task importTask = sut.Import(destinationPath, 0, ulong.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), token);

        BlockTreeSuggestPacer? pacer = null;
        while (pacer is null)
        {
            token.ThrowIfCancellationRequested();
            pacer = sut.CurrentPacer;
            if (pacer is null) await Task.Yield();
        }
        await pacer.WaitForPausedAsync(token);

        Block expectedFinalizedBlock = outputCtx.Resolve<IBlockTree>().FindBlock(expectedStopBlock)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(maxSuggestedBlocks, Is.EqualTo(expectedStopBlock));
            Assert.That(inTree.FinalizedHash, Is.EqualTo(expectedFinalizedBlock.Hash));
            Assert.That(inTree.LastFinalizedBlockLevel, Is.EqualTo(expectedStopBlock));
        }
        shouldAdvanceMainChain = true;
        inTree.TryUpdateMainChain(inTree.FindBlock(expectedStopBlock, BlockTreeLookupOptions.None)!.Header, true, preloadedBlocks: new[] { inTree.FindBlock(expectedStopBlock, BlockTreeLookupOptions.None)! });

        await importTask;
    }

    [CancelAfter(2000)]
    [Test]
    public async Task ImportAsArchiveSync_WhenStartIsLessThanHead_ShouldThrow(CancellationToken token)
    {
        await using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        string destinationPath = outputCtx.ResolveTempDirPath();

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        await using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                ImportBlocksBufferSize = 10,
                MaxEra1Size = 16,
                NetworkName = EraTestModule.TestNetwork
            })
            .Build();

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Func<Task> act = () => sut.Import(destinationPath, 30, ulong.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), token);

        Assert.That(async () => await act(), Throws.TypeOf<ArgumentException>());
    }
}

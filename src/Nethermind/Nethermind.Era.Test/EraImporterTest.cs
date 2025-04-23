// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using System.IO.Abstractions;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
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
        using IContainer testContext = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(10).Build();
        string tempDirectory = testContext.ResolveTempDirPath();

        IFileSystem fileSystem = testContext.Resolve<IFileSystem>();
        fileSystem.Directory.CreateDirectory(tempDirectory);

        await fileSystem.File.WriteAllBytesAsync(Path.Join(tempDirectory, EraExporter.ChecksumsFileName), []);

        string badFilePath = Path.Join(tempDirectory, "abc-00000-00000000.era1");
        FileSystemStream stream = fileSystem.File.Create(badFilePath);
        await stream.WriteAsync(new byte[] { 0, 0 });
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
        await sut.Import(destinationPath, 0, long.MaxValue, Path.Join(destinationPath, EraExporter.AccumulatorFileName), default);
    }

    [Test]
    public async Task VerifyEraFiles_VerifyAccumulatorsWithUnexpected_ThrowEraVerificationException()
    {
        using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        IFileSystem fileSystem = outputCtx.Resolve<IFileSystem>();
        string destinationPath = outputCtx.ResolveTempDirPath();

        string accumulatorPath = Path.Combine(destinationPath, EraExporter.AccumulatorFileName);
        var accumulators = outputCtx.Resolve<IFileSystem>().File.ReadAllLines(accumulatorPath).Select(s => Bytes.FromHexString(s)).ToArray();
        accumulators[accumulators.Length - 1] = new byte[32];
        await fileSystem.File.WriteAllLinesAsync(accumulatorPath, accumulators.Select(acc => acc.ToHexString()));

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;
        using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Func<Task> importTask = () => sut.Import(destinationPath, 0, long.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), default);

        Assert.That(importTask, Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task VerifyEraFiles_ModifiedChecksum_ThrowEraVerificationException()
    {
        using IContainer outputCtx = await EraTestModule.CreateExportedEraEnv(64);
        IFileSystem fileSystem = outputCtx.Resolve<IFileSystem>();
        string destinationPath = outputCtx.ResolveTempDirPath();

        string checksumPath = Path.Combine(destinationPath, EraExporter.ChecksumsFileName);
        var checksums = outputCtx.Resolve<IFileSystem>().File.ReadAllLines(checksumPath).Select(s => Bytes.FromHexString(s)).ToArray();
        checksums[checksums.Length - 1] = new byte[32];
        await fileSystem.File.WriteAllLinesAsync(checksumPath, checksums.Select(acc => acc.ToHexString()));

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outputCtx.Resolve<IBlockTree>().FindBlock(0, BlockTreeLookupOptions.None)!).TestObject;
        using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .Build();

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Func<Task> importTask = () => sut.Import(destinationPath, 0, long.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), default);

        Assert.That(importTask, Throws.TypeOf<EraVerificationException>());
    }

    [CancelAfter(2000)]
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

        ManualResetEventSlim reachedBlock11 = new ManualResetEventSlim();
        bool shouldUpdateMainChain = false;
        long maxSuggestedBlocks = 0;
        long expectedStopBlock = 10;
        inTree.NewBestSuggestedBlock += (sender, args) =>
        {
            if (shouldUpdateMainChain) inTree.UpdateMainChain([args.Block], true);
            maxSuggestedBlocks = args.Block.Number;
            if (args.Block.Number == expectedStopBlock) reachedBlock11.Set();
        };

        IEraImporter sut = inCtx.Resolve<IEraImporter>();
        Task importTask = sut.Import(destinationPath, 0, long.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), token);

        reachedBlock11.Wait(token);
        await Task.Delay(100);

        maxSuggestedBlocks.Should().Be(expectedStopBlock);
        shouldUpdateMainChain = true;
        inTree.UpdateMainChain([inTree.FindBlock(expectedStopBlock, BlockTreeLookupOptions.None)!], true);

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
        Func<Task> act = () => sut.Import(destinationPath, 30, long.MaxValue,
            Path.Join(destinationPath, EraExporter.AccumulatorFileName), token);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

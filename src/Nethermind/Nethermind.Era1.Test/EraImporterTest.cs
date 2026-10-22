// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Test.Builders;
using System.IO.Abstractions;
using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Caching;
using Nethermind.Crypto;
using Nethermind.Era1.Exceptions;
using NSubstitute;
using Nethermind.Blockchain.Synchronization;

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
    public async Task ImportAsArchiveSync_ValidatesBlockAgainstItsParent()
    {
        await using IContainer testCtx = await EraTestModule.CreateExportedEraEnv(64);
        Block genesis = testCtx.Resolve<IBlockTree>().FindBlock(0)!;

        IBlockTree blockTree = Build.A.BlockTree().TestObject;
        blockTree.SuggestBlock(genesis, BlockTreeSuggestOptions.None);

        // The archive's own verification passes; only the parent-relative rules, which processing cannot catch, reject.
        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>()).Returns(true);
        blockValidator.ValidateOrphanedBlock(Arg.Any<Block>(), out Arg.Any<string?>()).Returns(true);
        blockValidator.ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out Arg.Any<string?>(), Arg.Any<bool>()).Returns(false);

        await using IContainer targetCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(blockTree)
            .AddSingleton<IBlockValidator>(blockValidator)
            .Build();

        IEraImporter sut = targetCtx.Resolve<IEraImporter>();
        Assert.That(() => sut.Import(testCtx.ResolveTempDirPath(), 0, 0, null, CancellationToken.None), Throws.TypeOf<EraVerificationException>());
        blockValidator.Received().ValidateSuggestedBlock(Arg.Is<Block>(b => b.Number == 1), Arg.Is<BlockHeader>(p => p.Hash == genesis.Hash), out Arg.Any<string?>(), Arg.Any<bool>());
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

    public enum ImportedHeader
    {
        Valid,
        ProofOfWorkAfterProofOfStake,
        ProofOfWorkAfterTerminalBlock,
        NonIncreasingTimestamp,
        UnknownParent
    }

    [Test]
    public async Task Import_WithRealValidator([Values] ImportedHeader importedHeader)
    {
        bool terminalParent = importedHeader == ImportedHeader.ProofOfWorkAfterTerminalBlock;
        bool proofOfWorkChild = importedHeader is ImportedHeader.ProofOfWorkAfterProofOfStake or ImportedHeader.ProofOfWorkAfterTerminalBlock;
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = 1_000_000 };
        Block genesis = Build.A.Block.Genesis.WithDifficulty(terminalParent ? 1UL : 1_000_000UL).WithTimestamp(1_663_000_000).TestObject;
        BlockBuilder parentBuilder = Build.A.Block.WithParent(genesis).WithPostMergeRules();
        if (terminalParent)
            parentBuilder.WithDifficulty(999_999).WithPostMergeFlag(false);
        Block parent = parentBuilder.TestObject;
        BlockBuilder childBuilder = Build.A.Block.WithParent(parent).WithPostMergeRules().WithTotalDifficulty(1_000_000UL);
        if (importedHeader == ImportedHeader.NonIncreasingTimestamp)
            childBuilder.WithTimestamp(parent.Timestamp);
        Block child = childBuilder.TestObject;
        Block targetParent = importedHeader == ImportedHeader.UnknownParent
            ? Build.A.Block.WithParent(genesis).WithPostMergeRules().WithExtraData([1]).TestObject
            : parent;
        BlockTreeBuilder targetTreeBuilder = new(genesis, specProvider);
        BlockTree targetTree = targetTreeBuilder.WithBlocks(genesis, targetParent).TestObject;
        targetTree.TryUpdateMainChain(targetParent.Header, true, forceUpdateHeadBlock: true, preloadedBlocks: [targetParent]);
        await using IContainer targetCtx = new ContainerBuilder()
            .AddModule(new EraTestModule(useRealValidator: true))
            .AddModule(new EthHashModule(new MiningConfig()))
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<ITimestamper>(new Timestamper(1_663_000_100L))
            .AddSingleton(new ChainSpec { Genesis = genesis, Parameters = new ChainParameters { TerminalTotalDifficulty = 1_000_000 } })
            .AddSingleton<IMergeConfig>(new MergeConfig())
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .Build();
        if (proofOfWorkChild)
        {
            child.Header.IsPostMerge = false;
            child.Header.Difficulty = targetCtx.Resolve<IDifficultyCalculator>().Calculate(child.Header, parent.Header);
            // Self-consistent archive metadata can claim pre-merge TD; the local parent establishes consensus.
            child.Header.TotalDifficulty = child.Difficulty;
            child.Header.Hash = child.Header.CalculateHash();
        }
        string exportPath = targetCtx.ResolveTempDirPath();
        Directory.CreateDirectory(exportPath);
        string fileName = EraPathUtils.Filename(EraTestModule.TestNetwork, 0, Keccak.Zero);
        ValueHash256 checksum;
        using (EraWriter writer = new(Path.Join(exportPath, fileName), specProvider))
        {
            await writer.Add(child, []);
            (_, checksum) = await writer.Finalize();
        }
        await File.WriteAllTextAsync(Path.Join(exportPath, EraExporter.ChecksumsFileName), $"{checksum} {fileName}");
        IEraImporter importer = targetCtx.Resolve<IEraImporter>();
        Hash256? finalizedBeforeImport = targetTree.FinalizedHash;
        if (terminalParent)
        {
            targetCtx.Resolve<IPoSSwitcher>();
            ((IClearableCache)targetTreeBuilder.HeaderStore).ClearCache();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targetTree.FindHeader(parent.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.TotalDifficulty, Is.Null);
                Assert.That(targetTree.GetInfo(parent.Number, parent.Hash!).Info?.TotalDifficulty, Is.EqualTo(specProvider.TerminalTotalDifficulty));
            }
        }

        if (importedHeader == ImportedHeader.Valid)
        {
            await importer.Import(exportPath, 2, 2, null, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targetTree.FindBlock(child.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
                Assert.That(targetTree.FinalizedHash, Is.EqualTo(child.Hash));
            }
        }
        else
        {
            string expectedError = proofOfWorkChild
                ? "A proof-of-work block cannot follow a terminal or proof-of-stake parent."
                : BlockErrorMessages.InvalidTimestamp;
            Assert.That(() => importer.Import(exportPath, 2, 2, null, CancellationToken.None),
                importedHeader == ImportedHeader.UnknownParent
                    ? Throws.TypeOf<EraImportException>().With.Message.Contains("Unknown parent")
                    : Throws.TypeOf<EraVerificationException>().With.Message.Contains(expectedError));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targetTree.FindBlock(child.Hash!, BlockTreeLookupOptions.None), Is.Null);
                Assert.That(targetTree.Head!.Hash, Is.EqualTo(targetParent.Hash));
                Assert.That(targetTree.FinalizedHash, Is.EqualTo(finalizedBeforeImport));
            }
        }
    }

    [Test]
    public async Task Import_EpochForeignToLocalHeaders_KeepsLocalHeaders([Values] bool withTrustedAccumulators, [Values(1UL, 9UL)] ulong lowestLocalHeader)
    {
        const int chainLength = 16;
        await using IContainer sourceCtx = await EraTestModule.CreateExportedEraEnv(chainLength);
        string exportPath = sourceCtx.ResolveTempDirPath();
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();

        await using IContainer targetCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<ISyncConfig>(new SyncConfig { FastSync = true, SnapSync = true })
            .Build();
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        targetTree.SuggestBlock(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!);

        Hash256[] localHashes = new Hash256[chainLength];
        for (ulong i = lowestLocalHeader; i < chainLength; i++)
        {
            BlockHeader local = sourceTree.FindHeader(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Clone();
            local.ExtraData = [0x4c, 0x4f, 0x43, (byte)i];
            local.Hash = local.CalculateHash();
            targetTree.Insert(local, BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            localHashes[i] = local.Hash;
        }

        string? accumulatorFile = withTrustedAccumulators ? Path.Join(exportPath, EraExporter.AccumulatorFileName) : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, ulong.MaxValue, accumulatorFile, CancellationToken.None),
                Throws.TypeOf<EraVerificationException>());
            for (ulong i = 1; i < chainLength; i++)
                Assert.That(targetTree.FindLevel(i)?.BlockInfos.Select(static info => info.BlockHash), i < lowestLocalHeader ? Is.Null : Is.EqualTo(new[] { localHashes[i] }), $"headers at {i}");
        }
    }

    [Test]
    public async Task EnsureHeaderlessHeightsLinkToKnownHeaders_CancelledOverKnownHeights_ThrowsWithoutReadingArchive()
    {
        const int chainLength = 16;
        await using IContainer ctx = EraTestModule.BuildContainerBuilderWithBlockTreeOfLength(chainLength).Build();
        int archiveReads = 0;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => KnownHeaders.EnsureHeaderlessHeightsLinkToKnownHeaders(
                    ctx.Resolve<IBlockTree>(),
                    1,
                    chainLength - 1,
                    _ =>
                    {
                        archiveReads++;
                        return Task.FromResult<Block?>(null);
                    },
                    new CancellationToken(canceled: true)),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(archiveReads, Is.Zero);
        }
    }

    [Test]
    public void EnsureHeaderlessHeightsLinkToKnownHeaders_HeadersCoverRange_DoesNotLoadEveryLevel()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestKnownNumber.Returns(1_000_000UL);
        blockTree.BestKnownBeaconNumber.Returns(0UL);
        blockTree.LowestInsertedHeader.Returns(Build.A.BlockHeader.WithNumber(1).TestObject);
        blockTree.LowestInsertedBeaconHeader.Returns((BlockHeader?)null);
        blockTree.FindLevel(Arg.Any<ulong>()).Returns(new ChainLevelInfo(true, new BlockInfo(Keccak.Zero, 0)));

        Assert.That(
            () => KnownHeaders.EnsureHeaderlessHeightsLinkToKnownHeaders(blockTree, 1, 1_000, _ => Task.FromResult<Block?>(null), CancellationToken.None),
            Throws.Nothing);

        Assert.That(blockTree.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IBlockTree.FindLevel)), Is.LessThan(10));
    }
}

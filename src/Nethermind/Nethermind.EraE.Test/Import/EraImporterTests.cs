// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core.Specs;
using Nethermind.Merge.Plugin;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Messages;
using Nethermind.Core.Caching;
using Nethermind.Crypto;
using Nethermind.Core.Test.Builders;
using EraException = Nethermind.Era1.Exceptions.EraException;
using EraVerificationException = Nethermind.Era1.Exceptions.EraVerificationException;
using Nethermind.EraE.Export;
using Nethermind.EraE.Import;
using NSubstitute;
using NUnit.Framework;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Nethermind.Blockchain.Headers;
using Nethermind.EraE.Config;
using Nethermind.Logging;
using EraImportException = Nethermind.Era1.EraImportException;
using KnownHeaders = Nethermind.Era1.KnownHeaders;
using Nethermind.EraE.Store;

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
            System.IO.Path.Combine(tmpDirectory, EraExporter.ChecksumsSHA256FileName), "");

        IEraImporter sut = ctx.Resolve<IEraImporter>();
        Assert.That(
            () => sut.Import(tmpDirectory, 0, 0, null),
            Throws.TypeOf<EraException>());
    }

    [Test]
    public async Task Import_WithValidEraFiles_ImportsAllBlocksIntoTree()
    {
        const int chainLength = 32;
        await using ImportEnvironment env = await CreateImportEnvironment(chainLength);

        await env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null);

        for (ulong i = 1; i < chainLength; i++)
        {
            Assert.That(env.TargetTree.FindBlock(i, BlockTreeLookupOptions.None), Is.Not.Null, $"block {i} should have been imported");
        }

        Block expectedFinalizedBlock = env.SourceCtx.Resolve<IBlockTree>().FindBlock((ulong)chainLength - 1)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.TargetTree.FinalizedHash, Is.EqualTo(expectedFinalizedBlock.Hash));
            Assert.That(env.TargetTree.LastFinalizedBlockLevel, Is.EqualTo((ulong)chainLength - 1));
        }
    }

    [Test]
    public async Task Import_WithTrustedAccumulators_Succeeds()
    {
        await using ImportEnvironment env = await CreateImportEnvironment();
        string accumulatorPath = System.IO.Path.Combine(env.ExportPath, EraExporter.AccumulatorFileName);

        Assert.That(
            () => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, accumulatorPath),
            Throws.Nothing);
    }

    [Test]
    public async Task Import_WithModifiedChecksum_ThrowsEraVerificationException()
    {
        await using ImportEnvironment env = await CreateImportEnvironment();

        string checksumPath = System.IO.Path.Combine(env.ExportPath, EraExporter.ChecksumsSHA256FileName);
        string[] lines = await System.IO.File.ReadAllLinesAsync(checksumPath);
        lines[^1] = "0x0000000000000000000000000000000000000000000000000000000000000000 " +
                    System.IO.Path.GetFileName(lines[^1].Split(' ')[^1]);
        await System.IO.File.WriteAllLinesAsync(checksumPath, lines);

        Assert.That(
            () => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WithWrongTrustedAccumulator_ThrowsEraVerificationException()
    {
        await using ImportEnvironment env = await CreateImportEnvironment();

        string fakeAccumulatorPath = System.IO.Path.Combine(env.ExportPath, "fake_accumulators.txt");
        string[] accLines = await System.IO.File.ReadAllLinesAsync(
            System.IO.Path.Combine(env.ExportPath, EraExporter.AccumulatorFileName));
        string[] fakeLines = new string[accLines.Length];
        for (int i = 0; i < accLines.Length; i++)
            fakeLines[i] = "0x0000000000000000000000000000000000000000000000000000000000000000 " +
                           accLines[i].Split(' ')[^1];
        await System.IO.File.WriteAllLinesAsync(fakeAccumulatorPath, fakeLines);

        Assert.That(
            () => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, fakeAccumulatorPath),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_WithPartialRange_ImportsOnlyRequestedBlocks()
    {
        await using ImportEnvironment env = await CreateImportEnvironment();

        await env.Sut.Import(env.ExportPath, 0, 15, null);

        for (ulong i = 1; i <= 15; i++)
            Assert.That(env.TargetTree.FindBlock(i, BlockTreeLookupOptions.None), Is.Not.Null, $"block {i} should have been imported");

        Assert.That(env.TargetTree.FindBlock(16, BlockTreeLookupOptions.None), Is.Null, "block 16 is outside the requested range");
    }

    [Test]
    public async Task Import_WhenCalledTwice_DoesNotThrowAndIsIdempotent()
    {
        await using ImportEnvironment env = await CreateImportEnvironment();

        await env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null);

        Assert.That(() => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null), Throws.Nothing,
            "re-importing the same range must be idempotent");
    }

    [Test]
    public async Task ExportThenImport_RoundTrip_BlocksAndReceiptsMatchOriginal()
    {
        const int chainLength = 32;
        await using ImportEnvironment env = await CreateImportEnvironment(
            chainLength,
            b => b.AddSingleton<ISyncConfig>(new SyncConfig { FastSync = true }));

        IReceiptStorage sourceReceipts = env.SourceCtx.Resolve<IReceiptStorage>();

        await env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null);

        IReceiptStorage targetReceipts = env.TargetCtx.Resolve<IReceiptStorage>();
        IBlockTree sourceTree = env.SourceCtx.Resolve<IBlockTree>();

        for (ulong i = 1; i < chainLength; i++)
        {
            Block? original = sourceTree.FindBlock(i, BlockTreeLookupOptions.None);
            Block? imported = env.TargetTree.FindBlock(i, BlockTreeLookupOptions.None);

            Assert.That(imported, Is.Not.Null, $"block {i} should exist after import");
            Assert.That(imported!.Hash, Is.EqualTo(original!.Hash!), $"block {i} hash must match");

            TxReceipt[] originalReceipts = sourceReceipts.Get(original!);
            bool hasReceipts = targetReceipts.HasBlock(imported.Number, imported.Hash!);
            if (originalReceipts.Length > 0)
                Assert.That(hasReceipts, Is.True, $"receipts for block {i} should have been imported");
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

        await targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, ulong.MaxValue, null);

        IReceiptStorage sourceReceipts = sourceCtx.Resolve<IReceiptStorage>();
        IReceiptStorage targetReceipts = targetCtx.Resolve<IReceiptStorage>();

        for (ulong i = 1; i < chainLength; i++)
        {
            Block? original = sourceTree.FindBlock(i, BlockTreeLookupOptions.None);
            Block? imported = targetTree.FindBlock(i, BlockTreeLookupOptions.None);

            Assert.That(imported, Is.Not.Null, $"post-merge block {i} should exist after import");
            Assert.That(imported!.Hash, Is.EqualTo(original!.Hash!), $"post-merge block {i} hash must match");

            TxReceipt[] originalReceipts = sourceReceipts.Get(original!);
            bool hasReceipts = targetReceipts.HasBlock(imported.Number, imported.Hash!);
            if (originalReceipts.Length > 0)
                Assert.That(hasReceipts, Is.True, $"receipts for post-merge block {i} should have been imported");
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

        for (ulong i = 1; i < chainLength; i++)
        {
            Block block = sourceTree.FindBlock(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
            targetTree.Insert(block,
                BlockTreeInsertBlockOptions.SaveHeader | BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks,
                BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
        }

        await using IContainer targetCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree)
            .Build();

        await targetCtx.Resolve<IEraImporter>().Import(exportPath, 0, ulong.MaxValue, null);

        for (ulong i = 1; i < chainLength; i++)
        {
            Block? imported = targetTree.FindBlock(i, BlockTreeLookupOptions.None);
            Assert.That(imported, Is.Not.Null, $"block {i} should exist");
            Assert.That(imported!.TotalDifficulty, Is.Not.Null, $"block {i} should have TotalDifficulty after import");
        }
    }

    [Test]
    public async Task Import_WhenBlockFailsValidation_ThrowsEraVerificationException()
    {
        await using ImportEnvironment env = await CreateImportEnvironment(
            configure: b => b.AddSingleton<IBlockValidator>(Always.Invalid));

        Assert.That(
            () => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
    }

    [Test]
    public async Task Import_ValidatesBlockAgainstItsParent()
    {
        // The archive's own verification passes; only the parent-relative rules, which processing cannot catch, reject.
        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateBodyAgainstHeader(Arg.Any<BlockHeader>(), Arg.Any<BlockBody>(), out Arg.Any<string?>()).Returns(true);
        blockValidator.ValidateSuggestedBlock(Arg.Any<Block>(), Arg.Any<BlockHeader>(), out Arg.Any<string?>(), Arg.Any<bool>()).Returns(false);
        await using ImportEnvironment env = await CreateImportEnvironment(
            configure: b => b.AddSingleton<IBlockValidator>(blockValidator));
        Hash256 genesisHash = env.TargetTree.Genesis!.Hash!;

        Assert.That(
            () => env.Sut.Import(env.ExportPath, 0, ulong.MaxValue, null),
            Throws.TypeOf<EraVerificationException>());
        blockValidator.Received().ValidateSuggestedBlock(Arg.Is<Block>(b => b.Number == 1), Arg.Is<BlockHeader>(p => p.Hash == genesisHash), out Arg.Any<string?>(), Arg.Any<bool>());
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
        TestSpecProvider specProvider = new(Paris.Instance) { TerminalTotalDifficulty = terminalParent ? 1_000_000UL : 0UL };
        Block genesis = Build.A.Block.Genesis.WithPostMergeRules().WithDifficulty(terminalParent ? 1UL : 0UL).WithTimestamp(1_663_000_000).TestObject;
        BlockBuilder parentBuilder = Build.A.Block.WithParent(genesis).WithPostMergeRules();
        if (terminalParent)
            parentBuilder.WithDifficulty(999_999).WithPostMergeFlag(false);
        Block parent = parentBuilder.TestObject;
        Block targetParent = importedHeader == ImportedHeader.UnknownParent
            ? Build.A.Block.WithParent(genesis).WithPostMergeRules().WithExtraData([1]).TestObject
            : parent;
        BlockTreeBuilder targetTreeBuilder = new(genesis, specProvider);
        BlockTree targetTree = targetTreeBuilder.WithBlocks(genesis, targetParent).TestObject;
        targetTree.TryUpdateMainChain(targetParent.Header, true, forceUpdateHeadBlock: true, preloadedBlocks: [targetParent]);
        await using IContainer targetCtx = new ContainerBuilder()
            .AddModule(new EraETestModule(useRealValidator: true))
            .AddModule(new EthHashModule(new MiningConfig()))
            .AddSingleton<IBlockTree>(targetTree)
            .AddSingleton<ISpecProvider>(specProvider)
            .AddSingleton<ITimestamper>(new Timestamper(1_663_000_100L))
            .AddSingleton(new ChainSpec { Genesis = genesis, Parameters = new ChainParameters { TerminalTotalDifficulty = specProvider.TerminalTotalDifficulty } })
            .AddSingleton<IMergeConfig>(new MergeConfig())
            .AddSingleton<IPoSSwitcher, PoSSwitcher>()
            .AddDecorator<IHeaderValidator, MergeHeaderValidator>()
            .AddDecorator<ISealValidator, MergeSealValidator>()
            .Build();
        BlockBuilder childBuilder = Build.A.Block.WithParent(parent).WithPostMergeRules();
        if (importedHeader == ImportedHeader.NonIncreasingTimestamp)
            childBuilder.WithTimestamp(parent.Timestamp);
        Block child = childBuilder.TestObject;
        if (proofOfWorkChild)
        {
            child.Header.IsPostMerge = false;
            child.Header.Difficulty = targetCtx.Resolve<IDifficultyCalculator>().Calculate(child.Header, parent.Header);
            child.Header.Hash = child.Header.CalculateHash();
        }
        Block descendant = Build.A.Block.WithParent(child).WithPostMergeRules().TestObject;
        BlockTree sourceTree = new BlockTreeBuilder(genesis, new TestSpecProvider(Paris.Instance)).WithBlocks(genesis, parent, child, descendant).TestObject;
        sourceTree.TryUpdateMainChain(descendant.Header, true, forceUpdateHeadBlock: true, preloadedBlocks: [descendant]);

        IPoSSwitcher archiveConsensus = Substitute.For<IPoSSwitcher>();
        archiveConsensus.IsPostMerge(Arg.Any<BlockHeader>()).Returns(call => call.Arg<BlockHeader>().Difficulty == 0);
        await using IContainer sourceCtx = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IPoSSwitcher>(archiveConsensus)
            .AddSingleton<IBlockTree>(sourceTree)
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Paris.Instance))
            .Build();
        string exportPath = sourceCtx.ResolveTempDirPath();
        await sourceCtx.Resolve<IEraExporter>().Export(exportPath, 2, 3);

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
            await importer.Import(exportPath, 2, 3, null, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targetTree.FindBlock(child.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
                Assert.That(targetTree.FindBlock(descendant.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
                Assert.That(targetTree.FinalizedHash, Is.EqualTo(descendant.Hash));
            }
        }
        else
        {
            string expectedError = proofOfWorkChild
                ? "A proof-of-work block cannot follow a terminal or proof-of-stake parent."
                : BlockErrorMessages.InvalidTimestamp;
            Assert.That(() => importer.Import(exportPath, 2, 3, null, CancellationToken.None),
                importedHeader == ImportedHeader.UnknownParent
                    ? Throws.TypeOf<Nethermind.Era1.EraImportException>().With.Message.Contains("Unknown parent")
                    : Throws.TypeOf<EraVerificationException>().With.Message.Contains(expectedError));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(targetTree.FindBlock(child.Hash!, BlockTreeLookupOptions.None), Is.Null);
                Assert.That(targetTree.FindBlock(descendant.Hash!, BlockTreeLookupOptions.None), Is.Null);
                Assert.That(targetTree.Head!.Hash, Is.EqualTo(targetParent.Hash));
                Assert.That(targetTree.FinalizedHash, Is.EqualTo(finalizedBeforeImport));
            }
        }
    }

    private const BlockTreeInsertHeaderOptions MainChainHeader = BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
    private const BlockTreeInsertHeaderOptions BeaconHeader = BlockTreeInsertHeaderOptions.BeaconHeaderInsert | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;
    private const BlockTreeInsertHeaderOptions SideHeader = BlockTreeInsertHeaderOptions.NotOnMainChain | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded;

    [Test]
    public async Task Import_PostMergeEpochForeignToLocalHeaders_KeepsLocalHeaders([Values(MainChainHeader, BeaconHeader)] BlockTreeInsertHeaderOptions localHeaderOptions)
    {
        const ulong chainLength = 16;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        Hash256?[] localHashes = InsertForeignHeaders(targetTree, sourceTree, 1, chainLength, localHeaderOptions);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, ulong.MaxValue, null),
                Throws.TypeOf<EraVerificationException>());
            for (ulong i = 1; i < chainLength; i++)
                Assert.That(targetTree.FindLevel(i)!.BlockInfos.Select(static info => info.BlockHash), Is.EqualTo(new[] { localHashes[i] }), $"headers at {i}");
        }
    }

    [Test]
    public async Task Import_PostMergeEpochMatchingLocalHeaders_ImportsBodies(
        [Values(MainChainHeader, BeaconHeader)] BlockTreeInsertHeaderOptions localHeaderOptions,
        [Values(1UL, 9UL)] ulong firstLocalHeight)
    {
        const ulong chainLength = 16;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertArchiveHeaders(targetTree, sourceTree, firstLocalHeight, chainLength, localHeaderOptions);

        await targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, ulong.MaxValue, null);

        using (Assert.EnterMultipleScope())
        {
            for (ulong i = 1; i < chainLength; i++)
                Assert.That(targetTree.FindBlock(i, BlockTreeLookupOptions.None)?.Hash, Is.EqualTo(sourceTree.FindHeader(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Hash), $"body {i}");
        }
    }

    [TestCase(1UL, 9UL, MainChainHeader, null, TestName = "Import_ForeignArchive_NoHeaderBelowLowestLocal")]
    [TestCase(5UL, 9UL, MainChainHeader, null, TestName = "Import_ForeignArchive_NoHeaderBetweenMatchingAndForeignLocal")]
    [TestCase(1UL, 9UL, SideHeader, null, TestName = "Import_ForeignArchive_NoHeaderBelowLowestUnmarkedLocal")]
    [TestCase(1UL, 1UL, MainChainHeader, BeaconHeader, TestName = "Import_ForeignArchive_MainIsLocalBeaconMainIsArchive")]
    [TestCase(1UL, 1UL, BeaconHeader, SideHeader, TestName = "Import_ForeignArchive_BeaconMainIsLocalUnmarkedIsArchive")]
    public async Task Import_ForeignArchive_LeavesLocalLevelsUntouched(ulong firstHeightWithoutMatchingHeader, ulong firstLocalHeight, BlockTreeInsertHeaderOptions localOptions, BlockTreeInsertHeaderOptions? archiveHeaderOptions)
    {
        const ulong chainLength = 16;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertArchiveHeaders(targetTree, sourceTree, 1, firstHeightWithoutMatchingHeader, MainChainHeader);
        InsertForeignHeaders(targetTree, sourceTree, firstLocalHeight, chainLength, localOptions);
        if (archiveHeaderOptions is not null)
            InsertArchiveHeaders(targetTree, sourceTree, firstLocalHeight, chainLength, archiveHeaderOptions.Value);

        (bool, Hash256?)[] mainBefore = MainChainHashes(targetTree, chainLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, ulong.MaxValue, null),
                Throws.TypeOf<EraVerificationException>());
            Assert.That(MainChainHashes(targetTree, chainLength), Is.EqualTo(mainBefore));
            AssertNoArchiveBodies(targetTree, sourceTree, chainLength);
        }
    }

    [Test]
    public async Task Import_PreMergeArchiveWithForeignTotalDifficulty_KeepsLocalTotalDifficulty()
    {
        const int chainLength = 32;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree, static (_, block) =>
        {
            block.Header.TotalDifficulty += 1000;
            return block;
        });
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertArchiveHeaders(targetTree, sourceTree, 1, chainLength, BlockTreeInsertHeaderOptions.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, ulong.MaxValue, null),
                Throws.TypeOf<EraVerificationException>());
            for (ulong i = 1; i < chainLength; i++)
                Assert.That(targetTree.FindLevel(i)!.BlockInfos[0].TotalDifficulty, Is.EqualTo(sourceTree.FindHeader(i, BlockTreeLookupOptions.None)!.TotalDifficulty), $"total difficulty {i}");
        }
    }

    [Test]
    public async Task Import_PartialRangeNotLinkingToLocalHeaderAboveIt_LeavesHeaderlessHeightsEmpty()
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertForeignHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, BeaconHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, lowestLocalHeader - 1, null),
                Throws.TypeOf<EraVerificationException>());
            AssertNoLevels(targetTree, 1, lowestLocalHeader);
        }
    }

    [Test]
    public async Task Import_ArchiveBlockAboveHeaderlessHeightsHasAnotherNumber_LeavesHeaderlessHeightsEmpty()
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree, static (number, block) =>
        {
            if (number != lowestLocalHeader)
                return block;

            BlockHeader renumbered = block.Header.Clone();
            renumbered.Number = lowestLocalHeader - 1;
            renumbered.Hash = renumbered.CalculateHash();
            return new Block(renumbered, block.Body);
        });
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertForeignHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, BeaconHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, ulong.MaxValue, null),
                Throws.TypeOf<EraImportException>());
            AssertNoLevels(targetTree, 1, lowestLocalHeader);
        }
    }

    [Test]
    public async Task Import_ArchiveBlockBelowKnownHeaderHasAnotherNumber_LeavesHeaderlessHeightsEmpty()
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockHeader renumbered = sourceTree.FindHeader(lowestLocalHeader - 1, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Clone();
        renumbered.Number = lowestLocalHeader - 2;
        renumbered.Hash = renumbered.CalculateHash();
        BlockHeader local = sourceTree.FindHeader(lowestLocalHeader, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Clone();
        local.ParentHash = renumbered.Hash;
        local.Hash = local.CalculateHash();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree, (number, block) => number switch
        {
            lowestLocalHeader => new Block(local, block.Body),
            lowestLocalHeader - 1 => new Block(renumbered, block.Body),
            _ => block
        });
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        targetTree.Insert(local, BeaconHeader);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, lowestLocalHeader, null),
                Throws.TypeOf<EraImportException>());
            AssertNoLevels(targetTree, 1, lowestLocalHeader);
        }
    }

    [Test]
    public async Task EnsureHeaderlessHeightsLinkToKnownHeaders_CancelledWhileWalkingHeaderlessHeights_StopsReadingArchive()
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        InsertArchiveHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, BeaconHeader);
        using CancellationTokenSource cts = new();
        int archiveReads = 0;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => KnownHeaders.EnsureHeaderlessHeightsLinkToKnownHeaders(
                    targetTree,
                    1,
                    chainLength - 1,
                    number =>
                    {
                        archiveReads++;
                        cts.Cancel();
                        return Task.FromResult(sourceTree.FindBlock(number, BlockTreeLookupOptions.TotalDifficultyNotNeeded));
                    },
                    cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(archiveReads, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Import_ThroughRemoteStore_ChecksHeaderlessHeightsAgainstLocalHeaders([Values] bool localHeadersMatchArchive)
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        string eraFile = EraPathUtils.GetAllEraFiles(sourceCtx.ResolveTempDirPath(), EraETestModule.TestNetwork).Single();
        byte[] checksum = SHA256.HashData(await File.ReadAllBytesAsync(eraFile));
        IRemoteEraClient client = Substitute.For<IRemoteEraClient>();
        client.FetchManifestAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<uint, RemoteEraEntry> { [0] = new(Path.GetFileName(eraFile), checksum) });
        client.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                File.Copy(eraFile, callInfo.ArgAt<string>(1));
                return Task.CompletedTask;
            });
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree, configure: builder => builder
            .AddSingleton<IEraStoreFactory>(ctx => new EraStoreFactory(
                ctx.Resolve<ISpecProvider>(), ctx.Resolve<IBlockValidator>(), ctx.Resolve<IFileSystem>(), ctx.Resolve<IEraEConfig>(), ctx.Resolve<ILogManager>(), null, client)));
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        if (localHeadersMatchArchive)
            InsertArchiveHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, MainChainHeader);
        else
            InsertForeignHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, MainChainHeader);

        Directory.CreateDirectory(targetCtx.ResolveTempDirPath());
        Func<Task> import = () => targetCtx.Resolve<IEraImporter>().Import(targetCtx.ResolveTempDirPath(), 1, ulong.MaxValue, null);

        if (localHeadersMatchArchive)
        {
            await import();
            using (Assert.EnterMultipleScope())
            {
                for (ulong i = 1; i < chainLength; i++)
                    Assert.That(targetTree.FindBlock(i, BlockTreeLookupOptions.None)?.Hash, Is.EqualTo(sourceTree.FindHeader(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Hash), $"body {i}");
            }
        }
        else
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => import(), Throws.TypeOf<EraVerificationException>());
                AssertNoLevels(targetTree, 1, lowestLocalHeader);
            }
        }
    }

    [Test]
    public async Task Import_HeaderAboveRangeListedButNotStored_LeavesHeaderlessHeightsEmpty()
    {
        const ulong chainLength = 16;
        const ulong lowestLocalHeader = 9;
        await using IContainer sourceCtx = await EraETestModule.CreateExportedPostMergeEraEnv(chainLength);
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        await using IContainer targetCtx = CreateHeaderSyncTarget(sourceTree);
        IBlockTree targetTree = targetCtx.Resolve<IBlockTree>();
        Hash256?[] local = InsertForeignHeaders(targetTree, sourceTree, lowestLocalHeader, chainLength, MainChainHeader);
        targetCtx.Resolve<IHeaderStore>().Delete(local[lowestLocalHeader]!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                () => targetCtx.Resolve<IEraImporter>().Import(sourceCtx.ResolveTempDirPath(), 0, lowestLocalHeader - 1, null),
                Throws.TypeOf<EraVerificationException>());
            AssertNoLevels(targetTree, 1, lowestLocalHeader);
        }
    }

    private static IContainer CreateHeaderSyncTarget(IBlockTree sourceTree, Func<ulong, Block, Block>? rewriteArchiveBlock = null, Action<ContainerBuilder>? configure = null)
    {
        ContainerBuilder builder = EraETestModule.BuildContainerBuilder()
            .AddSingleton<ISyncConfig>(new SyncConfig { FastSync = true, SnapSync = true });
        configure?.Invoke(builder);
        if (rewriteArchiveBlock is not null)
            builder.AddDecorator<IEraStoreFactory>((_, factory) => new RewritingStoreFactory(factory, rewriteArchiveBlock));

        IContainer targetCtx = builder.Build();
        targetCtx.Resolve<IBlockTree>().SuggestBlock(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!);
        return targetCtx;
    }

    private static void InsertArchiveHeaders(IBlockTree targetTree, IBlockTree sourceTree, ulong from, ulong to, BlockTreeInsertHeaderOptions options)
    {
        BlockTreeLookupOptions lookup = (options & BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded) != 0
            ? BlockTreeLookupOptions.TotalDifficultyNotNeeded
            : BlockTreeLookupOptions.None;
        for (ulong i = from; i < to; i++)
            targetTree.Insert(sourceTree.FindHeader(i, lookup)!.Clone(), options);
    }

    private static Hash256?[] InsertForeignHeaders(IBlockTree targetTree, IBlockTree sourceTree, ulong from, ulong to, BlockTreeInsertHeaderOptions options)
    {
        Hash256?[] hashes = new Hash256?[to];
        for (ulong i = from; i < to; i++)
        {
            BlockHeader local = sourceTree.FindHeader(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Clone();
            local.ExtraData = [0x4c, 0x4f, 0x43, (byte)i];
            local.ParentHash = hashes[i - 1] ?? TestItem.KeccakA;
            local.Hash = local.CalculateHash();
            targetTree.Insert(local, options);
            hashes[i] = local.Hash;
        }
        return hashes;
    }

    private static void AssertNoLevels(IBlockTree targetTree, ulong from, ulong to)
    {
        for (ulong i = from; i < to; i++)
            Assert.That(targetTree.FindLevel(i), Is.Null, $"level {i}");
    }

    private static void AssertNoArchiveBodies(IBlockTree targetTree, IBlockTree sourceTree, ulong chainLength)
    {
        for (ulong i = 1; i < chainLength; i++)
            Assert.That(targetTree.FindBlock(sourceTree.FindHeader(i, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.Hash!, BlockTreeLookupOptions.TotalDifficultyNotNeeded, i), Is.Null, $"archive body {i}");
    }

    private sealed class RewritingStoreFactory(IEraStoreFactory inner, Func<ulong, Block, Block> rewrite) : IEraStoreFactory
    {
        public IEraStore Create(string src, ISet<ValueHash256>? trustedAccumulators) => new RewritingStore(inner.Create(src, trustedAccumulators), rewrite);
    }

    private sealed class RewritingStore(IEraStore inner, Func<ulong, Block, Block> rewrite) : IEraStore
    {
        public (ulong First, ulong Last) BlockRange => inner.BlockRange;
        public bool HasEpoch(ulong blockNumber) => inner.HasEpoch(blockNumber);
        public ulong NextEraStart(ulong blockNumber) => inner.NextEraStart(blockNumber);
        public void Dispose() => inner.Dispose();

        public async Task<(Block?, TxReceipt[]?)> FindBlockAndReceipts(ulong number, bool ensureValidated = true, CancellationToken cancellation = default)
        {
            (Block? block, TxReceipt[]? receipts) = await inner.FindBlockAndReceipts(number, ensureValidated, cancellation);
            return (block is null ? null : rewrite(number, block), receipts);
        }
    }

    private static (bool, Hash256?)[] MainChainHashes(IBlockTree tree, ulong chainLength)
    {
        (bool, Hash256?)[] result = new (bool, Hash256?)[chainLength];
        for (ulong i = 1; i < chainLength; i++)
        {
            ChainLevelInfo? level = tree.FindLevel(i);
            result[i] = (level?.HasBlockOnMainChain ?? false, level?.BlockInfos.FirstOrDefault()?.BlockHash);
        }
        return result;
    }

    private static async Task<ImportEnvironment> CreateImportEnvironment(
        int chainLength = 32,
        Action<ContainerBuilder>? configure = null)
    {
        IContainer sourceCtx = await EraETestModule.CreateExportedEraEnv(chainLength, from: 0, to: 0);
        string exportPath = sourceCtx.ResolveTempDirPath();
        IBlockTree sourceTree = sourceCtx.Resolve<IBlockTree>();
        BlockTree targetTree = Build.A.BlockTree()
            .WithBlocks(sourceTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;
        ContainerBuilder builder = EraETestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(targetTree);
        configure?.Invoke(builder);
        IContainer targetCtx = builder.Build();
        return new ImportEnvironment(sourceCtx, targetCtx, exportPath, targetTree, targetCtx.Resolve<IEraImporter>());
    }

    private sealed record ImportEnvironment(
        IContainer SourceCtx,
        IContainer TargetCtx,
        string ExportPath,
        BlockTree TargetTree,
        IEraImporter Sut) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await TargetCtx.DisposeAsync();
            await SourceCtx.DisposeAsync();
        }
    }
}

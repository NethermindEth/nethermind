// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [Test]
    [NonParallelizable]
    public async Task Stored_block_rejection_preserves_diagnostics_without_peer(
        [Values(AddBlockResult.UnknownParent, AddBlockResult.CannotAccept, AddBlockResult.InvalidBlock)] AddBlockResult rejection,
        [Values] bool maskSensitiveData)
    {
        bool previous = SensitiveLogMasking.Enabled;
        SensitiveLogMasking.Enabled = maskSensitiveData;
        try
        {
            BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder trees = BlockTreeTests.BlockTreeTestScenario
                .GoesLikeThis().WithBlockTrees(1, 2).InsertBeaconPivot(1).InsertBeaconBlocks(1, 1);
            TestLogger logger = new();
            await using IContainer node = CreateMergeNode(builder => builder
                    .AddSingleton<IBlockTree>(trees.NotSyncedTree)
                    .AddKeyedSingleton<IDb>(DbNames.Metadata, trees.NotSyncedTreeBuilder.MetadataDb)
                    .AddDecorator<IBlockTree>((_, tree) => new StoredBlockRejectionTree(tree, rejection))
                    .AddSingleton<ILogManager>(new OneLoggerLogManager(new(logger))),
                new MergeConfig { TerminalTotalDifficulty = "0" });
            PostMergeContext context = node.Resolve<PostMergeContext>();
            BlockHeader header = trees.SyncedTree.FindHeader(1UL, BlockTreeLookupOptions.None)!;
            context.BeaconPivot.EnsurePivot(header);
            context.BeaconPivot.ProcessDestination = header;
            context.ConfigureBestPeer(new PeerInfo(new SyncPeerMock(trees.SyncedTree, false, Response.AllCorrect, 16000000)));

            using BlocksRequest? request = await context.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
                DownloaderOptions.Process, 0, CancellationToken.None);

            string peer = maskSensitiveData ? "[redacted]" : string.Empty;
            string expected = rejection switch
            {
                AddBlockResult.UnknownParent => $"Peer {peer} sent orphaned blocks/headers inside the batch:",
                AddBlockResult.CannotAccept => $"Block download deferred: Block tree cannot accept block/header from peer {peer}:",
                _ => $"Peer {peer} sent an invalid block/header:"
            };
            using (Assert.EnterMultipleScope())
            {
                Assert.That(((StoredBlockRejectionTree)context.BlockTree).Rejections, Is.EqualTo(1));
                Assert.That(logger.LogList, Has.Some.Contains(expected));
                Assert.That(logger.LogList, Has.None.Contains(nameof(NullReferenceException)));
            }
        }
        finally
        {
            SensitiveLogMasking.Enabled = previous;
        }
    }

    private sealed class StoredBlockRejectionTree(IBlockTree tree, AddBlockResult rejection) : BlockTreeTestDouble(tree)
    {
        public int Rejections { get; private set; }

        public override AddBlockResult SuggestBlock(Block block, BlockTreeSuggestOptions options = BlockTreeSuggestOptions.ShouldProcess)
        {
            if (block.Number == 0) return base.SuggestBlock(block, options);
            Rejections++;
            return rejection;
        }
    }

    [TestCase(16UL, 32UL, false, 32UL, 32UL)]
    [TestCase(16UL, 32UL, false, 32UL, 29UL)]
    [TestCase(16UL, 32UL, true, 0UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8, true, 32UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8, true, 32UL, SyncBatchSizeMax * 8)]
    [TestCase(16UL, SyncBatchSizeMax * 8, false, 32UL, 32UL)]
    [TestCase(16UL, SyncBatchSizeMax * 8, false, 32UL, SyncBatchSizeMax * 8 - 16UL)]
    public async Task Merge_Happy_path(ulong beaconPivot, ulong headNumber, bool enableFastSync, ulong fastSyncLag, ulong insertedBeaconBlocks)
    {
        bool withReceipts = enableFastSync;
        ulong notSyncedTreeStartingBlockNumber = 3;

        InMemoryReceiptStorage? receiptStorage = withReceipts ? new InMemoryReceiptStorage() : null;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(notSyncedTreeStartingBlockNumber + 1, headNumber + 1, receiptStorage: receiptStorage)
            .InsertBeaconPivot(beaconPivot)
            .InsertBeaconHeaders(notSyncedTreeStartingBlockNumber + 1, beaconPivot - 1)
            .InsertBeaconBlocks(beaconPivot + 1, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees,
            new SyncConfig()
            {
                FastSync = enableFastSync,
                SnapSync = enableFastSync,
                StateMinDistanceFromHead = fastSyncLag
            },
            new MergeConfig()
            {
                TerminalTotalDifficulty = "0"
            });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None);

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        SyncPeerMock syncPeer = new(syncedTree, withReceipts, responseOptions, 16000000, receiptStorage: receiptStorage);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        if (enableFastSync)
        {
            await ctx.FastSyncUntilNoRequest(peerInfo);
            ulong expectedDownloadStart = notSyncedTreeStartingBlockNumber;
            ulong expectedDownloadEnd = ulong.Min(headNumber, insertedBeaconBlocks - fastSyncLag);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(ulong.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd)));
                Assert.That(ctx.BlockTree.BestKnownNumber, Is.EqualTo(ulong.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd)));
            }

            ulong receiptCount = 0;
            for (ulong i = expectedDownloadStart; i < expectedDownloadEnd; i++)
            {
                if (i % 3 == 0)
                {
                    receiptCount += 2;
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ctx.ReceiptStorage.Count, Is.EqualTo(withReceipts ? (int)receiptCount : 0));
                Assert.That(ctx.BeaconPivot.ProcessDestination?.Number, Is.EqualTo(ulong.Max(insertedBeaconBlocks - fastSyncLag, beaconPivot)));
            }
        }

        await ctx.FullSyncUntilNoRequest(peerInfo);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(insertedBeaconBlocks));
            Assert.That(syncPeer.RequestedBodyNumbers, Has.Member(notSyncedTreeStartingBlockNumber + 1), "Bodies of header-only beacon blocks come from the peer");
            Assert.That(syncPeer.RequestedBodyNumbers, Has.None.InRange(beaconPivot, insertedBeaconBlocks), "Bodies already stored by NewPayload are not requested again");
        }

        SuggestedStateRoots suggestedStateRoots = container.Resolve<SuggestedStateRoots>();
        ulong expectedFastSyncEnd = ulong.Max(notSyncedTreeStartingBlockNumber, insertedBeaconBlocks.SaturatingSub(fastSyncLag));
        if (insertedBeaconBlocks > expectedFastSyncEnd)
        {
            BlockTreeSuggestOptions[] processingOptions = suggestedStateRoots.SuggestedOptions
                .Where(options => options.ContainsFlag(BlockTreeSuggestOptions.ShouldProcess))
                .ToArray();
            Assert.That(processingOptions, Is.Not.Empty, "the test must observe at least one merge processing suggestion");
            Assert.That(processingOptions.All(options => options.ContainsFlag(BlockTreeSuggestOptions.NonBlockingProcessing)), Is.True,
                "merge processing suggestions must opt into asynchronous processing");
        }
        if (enableFastSync && expectedFastSyncEnd > notSyncedTreeStartingBlockNumber)
        {
            Assert.That(suggestedStateRoots.SuggestedOptions.Any(options =>
                !options.ContainsFlag(BlockTreeSuggestOptions.ShouldProcess) &&
                !options.ContainsFlag(BlockTreeSuggestOptions.NonBlockingProcessing)), Is.True,
                "merge fast-sync suggestions must not opt into asynchronous processing");
        }
    }

    [TestCase(null)]
    [TestCase(20UL)]
    public async Task Merge_downloads_only_bodies_not_stored_by_new_payload(ulong? corruptedBlockNumber)
    {
        const ulong beaconPivot = 16;
        const ulong lastBeaconBlock = 24;
        const ulong headNumber = 32;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, headNumber + 1)
            .InsertBeaconPivot(beaconPivot)
            .InsertBeaconHeaders(4, beaconPivot - 1)
            .InsertBeaconBlocks(beaconPivot + 1, lastBeaconBlock, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null)
            .InsertBeaconHeaders(lastBeaconBlock + 1, headNumber, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        if (corruptedBlockNumber is { } number)
        {
            Block stored = blockTrees.SyncedTree.FindBlock(number, BlockTreeLookupOptions.None)!;
            blockTrees.NotSyncedTree.Insert(new Block(stored.Header, new BlockBody([Build.A.Transaction.SignedAndResolved().TestObject], [], stored.Withdrawals)));
        }

        IBlockValidator blockValidator = Substitute.For<IBlockValidator>();
        blockValidator.ValidateSuggestedBlock(default!, default!, out _).ReturnsForAnyArgs(true);
        blockValidator.ValidateProcessedBlock(default!, default!, default!, out _).ReturnsForAnyArgs(true);
        blockValidator.ValidateBodyAgainstHeader(default!, default!, out _)
            .ReturnsForAnyArgs(static call => BlockValidator.ValidateTxRootMatchesTxs(call.ArgAt<BlockHeader>(0), call.ArgAt<BlockBody>(1), out _));

        await using IContainer container = CreateMergeNode(builder => builder
                .AddSingleton<IBlockTree>(blockTrees.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb)
                .AddSingleton(blockValidator)
                .AddDecorator<IBlockTree, BlockLookupRecordingBlockTree>(),
            new MergeConfig { TerminalTotalDifficulty = "0" });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();
        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(beaconPivot, BlockTreeLookupOptions.None);

        SyncPeerMock syncPeer = new(blockTrees.SyncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);

        IEnumerable<ulong> headerOnlyNumbers = Enumerable.Range(4, (int)(beaconPivot - 4))
            .Concat(Enumerable.Range((int)lastBeaconBlock + 1, (int)(headNumber - lastBeaconBlock)))
            .Select(static n => (ulong)n);
        Hash256[] headerOnlyHashes = headerOnlyNumbers.Select(n => blockTrees.SyncedTree.FindHeader(n, BlockTreeLookupOptions.None)!.Hash!).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(headNumber));
            Assert.That(syncPeer.RequestedBodyNumbers, Is.EquivalentTo(corruptedBlockNumber is { } corrupted ? headerOnlyNumbers.Append(corrupted) : headerOnlyNumbers));
            Assert.That(((BlockLookupRecordingBlockTree)ctx.BlockTree).LookedUpBlocks.Intersect(headerOnlyHashes), Is.Empty, "Blocks without a stored body are not looked up");
        }
    }

    [TestCase(32UL, DownloaderOptions.Insert, 32, false)]
    [TestCase(32UL, DownloaderOptions.Insert, 32, true)]
    public async Task Can_reach_terminal_block(ulong headNumber, int options, int threshold, bool withBeaconPivot)
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, headNumber + 1, true, ttd)
            .InsertBeaconPivot(16)
            .InsertBeaconHeaders(4, 15)
            .InsertBeaconBlocks(17, headNumber, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16UL, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);
        Assert.That(ctx.PoSSwitcher.HasEverReachedTerminalBlock(), Is.True);
    }

    [TestCase(32UL, 16, false, 16)]
    [TestCase(32UL, 16, true, 3)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(ulong headNumber, int ttdBlock, bool withBeaconPivot, int expectedBestKnownNumber)
    {
        UInt256 ttd = 10_000_000;
        int negativeTd = BlockHeaderBuilder.DefaultDifficulty.ToInt32(null);
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(
                4,
                headNumber + 1,
                true,
                ttd,
                syncedSplitFrom: ttdBlock,
                syncedSplitVariant: negativeTd
            );
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16UL, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);
        Assert.That(notSyncedTree.BestKnownNumber, Is.EqualTo(expectedBestKnownNumber));
    }

    [Test]
    public async Task Downloaded_pos_block_on_a_non_terminal_pow_parent_is_rejected()
    {
        UInt256 ttd = 1_000_000_000_000;
        TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = ttd };
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, 4, true, ttd);
        BlockTree localTree = blockTrees.NotSyncedTree;
        BlockTree remoteTree = blockTrees.SyncedTree;
        BlockHeader parent = remoteTree.Head!.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parent.Hash, Is.EqualTo(localTree.Head!.Hash));
            Assert.That(parent.TotalDifficulty, Is.LessThan(ttd));
        }

        // The chain continues past the zero-difficulty block.
        Block posBlock = Build.A.Block
            .WithParent(parent)
            .WithGasLimit(parent.GasLimit)
            .WithDifficulty(0)
            .WithNonce(0)
            .WithBaseFeePerGas(BaseFeeCalculator.Calculate(parent, specProvider.GetSpec(parent)))
            .TestObject;
        Block child = Build.A.Block
            .WithParent(posBlock)
            .WithGasLimit(posBlock.GasLimit)
            .WithDifficulty(parent.Difficulty)
            .WithBaseFeePerGas(BaseFeeCalculator.Calculate(posBlock.Header, specProvider.GetSpec(posBlock.Header)))
            .TestObject;
        remoteTree.SuggestBlock(posBlock);
        remoteTree.SuggestBlock(child);
        remoteTree.TryUpdateMainChain(child.Header, true, preloadedBlocks: [posBlock, child]);
        Assert.That(remoteTree.Head!.Hash, Is.EqualTo(child.Hash));

        await using IContainer node = CreateMergeNode(builder => builder
                .AddSingleton<IBlockTree>(localTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb)
                .AddDecorator<IBlockTree, StateTrackingBlockTree>()
                .AddSingleton<ISpecProvider>(specProvider)
                .AddSingleton<IBlockValidator, BlockValidator>(),
            new MergeConfig { TerminalTotalDifficulty = ttd.ToString() });
        PostMergeContext ctx = node.Resolve<PostMergeContext>();

        PeerInfo peerInfo = new(new SyncPeerMock(remoteTree, false, Response.AllCorrect, ttd * 1000));
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(localTree.Head!.Hash, Is.EqualTo(parent.Hash));
            Assert.That(localTree.IsKnownBlock(posBlock.Number, posBlock.Hash!), Is.False);
            Assert.That(localTree.IsKnownBlock(child.Number, child.Hash!), Is.False);
            Assert.That(() => ctx.PeerPool.Received().ReportBreachOfProtocol(peerInfo, DisconnectReason.ForwardSyncFailed,
                Arg.Is<string>(static details => details.Contains("A proof-of-stake block cannot follow a non-terminal proof-of-work parent."))), Throws.Nothing);
        }
    }

    [TestCase(32UL, 32UL, 0UL, 32UL)]
    [TestCase(32UL, 32UL, 10UL, 22UL)]
    public async Task WillSkipBlocksToIgnore(ulong pivot, ulong headNumber, ulong blocksToIgnore, ulong expectedBestKnownNumber)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, headNumber + 1)
            .InsertBeaconPivot(pivot)
            .InsertBeaconHeaders(4, pivot - 1);

        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees, new SyncConfig()
        {
            FastSync = true,
            SnapSync = true,
            StateMinDistanceFromHead = blocksToIgnore
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        Response responseOptions = Response.AllCorrect;

        SyncPeerMock syncPeer = new(syncedTree, false, responseOptions, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FastSyncUntilNoRequest(peerInfo);
        Assert.That(ctx.BlockTree.BestKnownNumber, Is.EqualTo(expectedBestKnownNumber));
    }

    [Test]
    public async Task Recalculate_header_total_difficulty()
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(1, 4, true, ttd);

        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;

        blockTrees
            .InsertOtherChainToMain(notSyncedTree, 1, 3) // Need to have the header inserted to LRU which mean we need to move the head forward
            .InsertBeaconHeaders(1, 3, tdMode: BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns((info =>
        {
            BlockHeader header = (BlockHeader)info[0];
            // Simulate something calls find header on the header, causing the TD to get recalculated
            notSyncedTree.FindHeader(header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            return true;
        }));

        await using IContainer container = CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(notSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, blockTrees.NotSyncedTreeBuilder.MetadataDb)
                .AddSingleton<ISealValidator>(sealValidator);
        }, new MergeConfig()
        {
            TerminalTotalDifficulty = $"{ttd}"
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        BlockHeader lastHeader = syncedTree.FindHeader(3, BlockTreeLookupOptions.None)!;
        // Because the FindHeader recalculated the TD.
        lastHeader.TotalDifficulty = 0;

        ctx.BeaconPivot.EnsurePivot(lastHeader);

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);

        Block? lastBestSuggestedBlock = null;

        notSyncedTree.NewBestSuggestedBlock += (_, args) =>
        {
            lastBestSuggestedBlock = args.Block;
        };

        await ctx.FullSyncUntilNoRequest(peerInfo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lastBestSuggestedBlock!.Hash, Is.EqualTo(lastHeader.Hash!));
            Assert.That(lastBestSuggestedBlock.TotalDifficulty, Is.Not.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public async Task BlockDownloader_works_correctly_with_withdrawals()
    {
        ulong fastSyncLag = 2;
        await using IContainer container = CreateMergeNode((_) =>
        {
        }, new SyncConfig()
        {
            FastSync = true,
            SnapSync = true,
            StateMinDistanceFromHead = fastSyncLag,
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        Response responseOptions = Response.AllCorrect | Response.WithTransactions;

        ulong headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        ulong chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, true, responseOptions, true);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockHeaders(ci.ArgAt<ulong>(0), ci.ArgAt<int>(1), ci.ArgAt<int>(2), ci.ArgAt<CancellationToken>(3)));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockBodies(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<ReadOnlyMemory<int>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await syncPeerInternal.GetReceipts(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(2)));


        syncPeer.TotalDifficulty.Returns(_ => syncPeerInternal.TotalDifficulty);
        syncPeer.HeadHash.Returns(_ => syncPeerInternal.HeadHash);
        syncPeer.HeadNumber.Returns(_ => syncPeerInternal.HeadNumber);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FastSyncUntilNoRequest(peerInfo);
        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(ulong.Min(headNumber, headNumber.SaturatingSub(fastSyncLag))));

        syncPeerInternal.ExtendTree(chainLength * 2);
        await ctx.FullSyncUntilNoRequest(peerInfo);
    }

    [Test]
    public void BlockDownloader_does_not_stop_processing_when_main_chain_is_unknown([Values(2UL, 6UL, 34UL, 129UL, 1024UL)] ulong pivot)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
             .GoesLikeThis()
             .WithBlockTrees(1, (int)(pivot + 1), false, 0)
             .InsertBeaconPivot(pivot)
             .InsertBeaconHeaders(1, pivot)
             .InsertBeaconBlocks(pivot, pivot, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);

        using IContainer container = CreateMergeNode(blockTrees, new MergeConfig()
        {
            TerminalTotalDifficulty = $"0"
        });

        PostMergeContext ctx = container.Resolve<PostMergeContext>();
        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        SyncPeerMock syncPeer = new(blockTrees.SyncedTree, true, Response.AllCorrect | Response.WithTransactions, 0);
        ctx.FullSyncUntilNoRequest(new PeerInfo(syncPeer));
    }

    private IContainer CreateMergeNode(Action<ContainerBuilder>? configurer = null, params IConfig[] configs)
    {
        IConfigProvider configProvider = new ConfigProvider(configs);
        return CreateNode((builder) =>
        {
            builder
                .AddModule(new TestMergeModule())
                .AddSingleton<PostMergeContext>();
            configurer?.Invoke(builder);
        }, configProvider);
    }

    private IContainer CreateMergeNode(BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder treeBuilder, params IConfig[] configs) =>
        CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(treeBuilder.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, treeBuilder.NotSyncedTreeBuilder.MetadataDb)
                .AddDecorator<IBlockTree, StateTrackingBlockTree>();
        }, configs);

    private sealed class BlockLookupRecordingBlockTree(IBlockTree blockTree) : BlockTreeTestDouble(blockTree)
    {
        private readonly ConcurrentQueue<Hash256> _lookedUpBlocks = new();

        public IReadOnlyCollection<Hash256> LookedUpBlocks => _lookedUpBlocks;

        public override Block? FindBlock(Hash256 blockHash, BlockTreeLookupOptions options, ulong? blockNumber = null)
        {
            _lookedUpBlocks.Enqueue(blockHash);
            return base.FindBlock(blockHash, options, blockNumber);
        }
    }

    private record PostMergeContext(
        IBeaconPivot BeaconPivot,
        IPoSSwitcher PoSSwitcher,
        ResponseBuilder ResponseBuilder,
        [KeyFilter(nameof(FastSyncFeed))] SyncFeedComponent<BlocksRequest> FastSyncFeedComponent,
        [KeyFilter(nameof(FullSyncFeed))] SyncFeedComponent<BlocksRequest> FullSyncFeedComponent,
        IForwardSyncController ForwardSyncController,
        IBlockTree BlockTree,
        InMemoryReceiptStorage ReceiptStorage,
        ISyncPeerPool PeerPool
    ) : Context(
        ResponseBuilder,
        FastSyncFeedComponent,
        FullSyncFeedComponent,
        ForwardSyncController,
        BlockTree,
        ReceiptStorage,
        PeerPool
    )
    {
        public void InsertBeaconHeaderFrom(SyncPeerMock syncPeer, ulong high, ulong low)
        {
            BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderInsert;
            for (ulong i = high; i >= low; --i)
            {
                BlockHeader? beaconHeader = syncPeer.BlockTree.FindHeader(i, BlockTreeLookupOptions.None)!;

                AddBlockResult insertResult = BlockTree!.Insert(beaconHeader!, headerOptions);
                Assert.That(insertResult, Is.EqualTo(AddBlockResult.Added));
                if (i == low) break;
            }
        }

        public override void ShouldFastSyncedUntil(ulong blockNumber) =>
            // With post merge, best suggested header always follow beacon pivot but not necessarily synced.
            // But BestSuggestedBody is updated, unlike PreMerge.
            // I don't make the rules
            Assert.That(BlockTree.BestSuggestedBody!.Number, Is.EqualTo(blockNumber));
    }
}

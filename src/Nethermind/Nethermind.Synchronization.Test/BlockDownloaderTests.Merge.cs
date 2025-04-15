// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(16L, 32L, false, 32, 32)]
    [TestCase(16L, 32L, false, 32, 29)]
    [TestCase(16L, 32L, true, 0, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, true, 32, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, false, 32, 32)]
    [TestCase(16L, SyncBatchSizeMax * 8, false, 32, SyncBatchSizeMax * 8 - 16L)]
    public async Task Merge_Happy_path(long beaconPivot, long headNumber, bool enableFastSync, int fastSyncLag, long insertedBeaconBlocks)
    {
        bool withReceipts = enableFastSync;
        int notSyncedTreeStartingBlockNumber = 3;

        InMemoryReceiptStorage? receiptStorage = withReceipts ? new InMemoryReceiptStorage() : null;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(notSyncedTreeStartingBlockNumber + 1, (int)headNumber + 1, receiptStorage: receiptStorage)
            .InsertBeaconPivot(beaconPivot)
            .InsertBeaconHeaders(notSyncedTreeStartingBlockNumber + 1, beaconPivot - 1)
            .InsertBeaconBlocks(beaconPivot + 1, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree syncedTree = blockTrees.SyncedTree;

        await using IContainer container = CreateMergeNode(blockTrees,
            new SyncConfig()
            {
                FastSync = enableFastSync,
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
            long expectedDownloadStart = notSyncedTreeStartingBlockNumber;
            long expectedDownloadEnd = Math.Min(headNumber, insertedBeaconBlocks - fastSyncLag);

            ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd));
            ctx.BlockTree.BestKnownNumber.Should().Be(Math.Max(notSyncedTreeStartingBlockNumber, expectedDownloadEnd));

            int receiptCount = 0;
            for (long i = expectedDownloadStart; i < expectedDownloadEnd; i++)
            {
                if (i % 3 == 0)
                {
                    receiptCount += 2;
                }
            }

            ctx.ReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
            ctx.BeaconPivot.ProcessDestination?.Number.Should().Be(Math.Max(insertedBeaconBlocks - fastSyncLag, beaconPivot));
        }

        await ctx.FullSyncUntilNoRequest(peerInfo);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(insertedBeaconBlocks);
    }

    [TestCase(32L, DownloaderOptions.Insert, 32, false)]
    [TestCase(32L, DownloaderOptions.Insert, 32, true)]
    public async Task Can_reach_terminal_block(long headNumber, int options, int threshold, bool withBeaconPivot)
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1, true, ttd)
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
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);
        Assert.That(ctx.PoSSwitcher.HasEverReachedTerminalBlock(), Is.True);
    }

    [TestCase(32L, 16, false, 16)]
    [TestCase(32L, 16, true, 3)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(long headNumber, int ttdBlock, bool withBeaconPivot, int expectedBestKnownNumber)
    {
        UInt256 ttd = 10_000_000;
        int negativeTd = BlockHeaderBuilder.DefaultDifficulty.ToInt32(null);
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(
                4,
                (int)headNumber + 1,
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
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FullSyncUntilNoRequest(peerInfo);
        notSyncedTree.BestKnownNumber.Should().Be(expectedBestKnownNumber);
    }

    [TestCase(32L, 32L, 0, 32)]
    [TestCase(32L, 32L, 10, 22)]
    public async Task WillSkipBlocksToIgnore(long pivot, long headNumber, int blocksToIgnore, long expectedBestKnownNumber)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(pivot)
            .InsertBeaconHeaders(4, pivot - 1);

        BlockTree syncedTree = blockTrees.SyncedTree;
        await using IContainer container = CreateMergeNode(blockTrees, new SyncConfig()
        {
            FastSync = true,
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
        ctx.BlockTree.BestKnownNumber.Should().Be(Math.Max(0, expectedBestKnownNumber));
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

        lastBestSuggestedBlock!.Hash.Should().Be(lastHeader.Hash!);
        lastBestSuggestedBlock.TotalDifficulty.Should().NotBeEquivalentTo(UInt256.Zero);
    }

    [Test]
    public async Task BlockDownloader_works_correctly_with_withdrawals()
    {
        int fastSyncLag = 2;
        await using IContainer container = CreateMergeNode((_) =>
        {
        }, new SyncConfig()
        {
            FastSync = true,
            StateMinDistanceFromHead = fastSyncLag,
        });
        PostMergeContext ctx = container.Resolve<PostMergeContext>();

        Response responseOptions = Response.AllCorrect | Response.WithTransactions;

        int headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, true, responseOptions, true);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockHeaders(ci.ArgAt<long>(0), ci.ArgAt<int>(1), ci.ArgAt<int>(2), ci.ArgAt<CancellationToken>(3)));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockBodies(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await syncPeerInternal.GetReceipts(ci.ArgAt<IReadOnlyList<Hash256>>(0), ci.ArgAt<CancellationToken>(1)));


        syncPeer.TotalDifficulty.Returns(_ => syncPeerInternal.TotalDifficulty);
        syncPeer.HeadHash.Returns(_ => syncPeerInternal.HeadHash);
        syncPeer.HeadNumber.Returns(_ => syncPeerInternal.HeadNumber);

        PeerInfo peerInfo = new(syncPeer);
        ctx.ConfigureBestPeer(peerInfo);
        await ctx.FastSyncUntilNoRequest(peerInfo);
        ctx.BlockTree.BestSuggestedHeader!.Number.Should().Be(Math.Max(0, Math.Min(headNumber, headNumber - fastSyncLag)));

        syncPeerInternal.ExtendTree(chainLength * 2);
        await ctx.FullSyncUntilNoRequest(peerInfo);
    }

    [TestCase(2)]
    [TestCase(6)]
    [TestCase(34)]
    [TestCase(129)]
    [TestCase(1024)]
    public void BlockDownloader_does_not_stop_processing_when_main_chain_is_unknown(long pivot)
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
                .AddModule(new MergeModule(configProvider))
                .AddSingleton<PostMergeContext>();
            configurer?.Invoke(builder);
        }, configProvider);
    }

    private IContainer CreateMergeNode(BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder treeBuilder, params IConfig[] configs)
    {
        return CreateMergeNode((builder) =>
        {
            builder
                .AddSingleton<IBlockTree>(treeBuilder.NotSyncedTree)
                .AddKeyedSingleton<IDb>(DbNames.Metadata, treeBuilder.NotSyncedTreeBuilder.MetadataDb);
        }, configs);
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
        public void InsertBeaconHeaderFrom(SyncPeerMock syncPeer, long high, long low)
        {
            BlockTreeInsertHeaderOptions headerOptions = BlockTreeInsertHeaderOptions.BeaconHeaderInsert;
            for (long i = high; i >= low; --i)
            {
                BlockHeader? beaconHeader = syncPeer.BlockTree.FindHeader(i, BlockTreeLookupOptions.None)!;

                AddBlockResult insertResult = BlockTree!.Insert(beaconHeader!, headerOptions);
                Assert.That(insertResult, Is.EqualTo(AddBlockResult.Added));
            }
        }

        public override void ShouldFastSyncedUntil(long blockNumber)
        {
            // With post merge, best suggested header always follow beacon pivot but not necessarily synced.
            // But BestSuggestedBody is updated, unlike PreMerge.
            // I don't make the rules
            BlockTree.BestSuggestedBody!.Number.Should().Be(blockNumber);
        }
    }
}

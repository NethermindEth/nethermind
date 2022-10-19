//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(16L, 32L, DownloaderOptions.Process, 32, 32)]
    [TestCase(16L, 32L, DownloaderOptions.Process, 32, 29)]
    [TestCase(16L, 32L, DownloaderOptions.WithReceipts | DownloaderOptions.MoveToMain, 0, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.WithReceipts | DownloaderOptions.MoveToMain, 32, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.Process, 32, 32)]
    [TestCase(16L, SyncBatchSize.Max * 8, DownloaderOptions.Process, 32, SyncBatchSize.Max * 8 - 16L)]
    public async Task Merge_Happy_path(long pivot, long headNumber, int options, int threshold, long insertedBeaconBlocks)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(pivot)
            .InsertBeaconHeaders(4, pivot - 1)
            .InsertBeaconBlocks(pivot + 1, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;
        Context ctx = new(notSyncedTree);
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.WithReceipts;
        InMemoryReceiptStorage receiptStorage = new();
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "0" }, new SyncConfig(), metadataDb, notSyncedTree,
            RopstenSpecProvider.Instance, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, notSyncedTree, LimboLogs.Instance);
        beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        beaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        MergeBlockDownloader downloader = new(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            notSyncedTree,
            Always.Valid,
            Always.Valid,
            NullSyncReport.Instance,
            receiptStorage,
            RopstenSpecProvider.Instance,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(notSyncedTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        SyncPeerMock syncPeer = new(syncedTree, withReceipts, responseOptions, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        ctx.BlockTree.BestSuggestedHeader.Number.Should().Be(Math.Max(0, insertedBeaconBlocks));
        ctx.BlockTree.BestKnownNumber.Should().Be(Math.Max(0, insertedBeaconBlocks));

        int receiptCount = 0;
        for (int i = (int)Math.Max(0, headNumber - threshold); i < peerInfo.HeadNumber; i++)
        {
            if (i % 3 == 0)
            {
                receiptCount += 2;
            }
        }

        receiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
        beaconPivot.ProcessDestination?.Number.Should().Be(insertedBeaconBlocks);
    }

    [TestCase(32L, DownloaderOptions.MoveToMain, 32, false)]
    [TestCase(32L, DownloaderOptions.MoveToMain, 32, true)]
    public async Task Can_reach_terminal_block(long headNumber, int options, int threshold, bool withBeaconPivot)
    {
        UInt256 ttd = 10000000;
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1, true, ttd)
            .InsertBeaconPivot(16)
            .InsertBeaconHeaders(4, 15)
            .InsertBeaconBlocks(17, headNumber, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;
        Context ctx = new(notSyncedTree);
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        InMemoryReceiptStorage receiptStorage = new();
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        RopstenSpecProvider specProvider = new();
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" }, new SyncConfig(), metadataDb, notSyncedTree,
            specProvider, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, notSyncedTree, LimboLogs.Instance);
        if (withBeaconPivot)
            beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        MergeBlockDownloader downloader = new(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            notSyncedTree,
            Always.Valid,
            Always.Valid,
            NullSyncReport.Instance,
            receiptStorage,
            specProvider,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(notSyncedTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        Assert.True(posSwitcher.HasEverReachedTerminalBlock());
    }

    [TestCase(32L, DownloaderOptions.MoveToMain, 16, false, 16)]
    [TestCase(32L, DownloaderOptions.MoveToMain, 16, true, 3)] // No beacon header, so it does not sync
    public async Task IfNoBeaconPivot_thenStopAtPoS(long headNumber, int options, int ttdBlock, bool withBeaconPivot, int expectedBestKnownNumber)
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

        Context ctx = new(notSyncedTree);
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        InMemoryReceiptStorage receiptStorage = new();
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        RopstenSpecProvider specProvider = new();
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" }, new SyncConfig(), metadataDb, notSyncedTree,
            specProvider, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, notSyncedTree, LimboLogs.Instance);
        if (withBeaconPivot)
            beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        MergeBlockDownloader downloader = new(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            notSyncedTree,
            Always.Valid,
            Always.Valid,
            NullSyncReport.Instance,
            receiptStorage,
            specProvider,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(notSyncedTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
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

        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;
        Context ctx = new(notSyncedTree);
        InMemoryReceiptStorage receiptStorage = new();
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "0" }, new SyncConfig(), metadataDb, notSyncedTree,
            RopstenSpecProvider.Instance, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, notSyncedTree, LimboLogs.Instance);
        beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        beaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        MergeBlockDownloader downloader = new(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            notSyncedTree,
            Always.Valid,
            Always.Valid,
            NullSyncReport.Instance,
            receiptStorage,
            RopstenSpecProvider.Instance,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(notSyncedTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);

        Response responseOptions = Response.AllCorrect;

        SyncPeerMock syncPeer = new(syncedTree, false, responseOptions, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        BlocksRequest blocksRequest = new BlocksRequest(DownloaderOptions.Process, blocksToIgnore);
        await downloader.DownloadBlocks(peerInfo, blocksRequest, CancellationToken.None);

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

        Context ctx = new(notSyncedTree);

        InMemoryReceiptStorage receiptStorage = new();
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" }, new SyncConfig(), metadataDb, notSyncedTree,
            RopstenSpecProvider.Instance, LimboLogs.Instance);

        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, notSyncedTree, LimboLogs.Instance);

        BlockHeader lastHeader = syncedTree.FindHeader(3, BlockTreeLookupOptions.None);
        // Because the FindHeader recalculated the TD.
        lastHeader.TotalDifficulty = 0;

        beaconPivot.EnsurePivot(lastHeader);

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns((info =>
        {
            BlockHeader header = (BlockHeader)info[0];
            // Simulate something calls find header on the header, causing the TD to get recalculated
            notSyncedTree.FindHeader(header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            return true;
        }));

        MergeBlockDownloader downloader = new(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            notSyncedTree,
            Always.Valid,
            sealValidator,
            NullSyncReport.Instance,
            receiptStorage,
            RopstenSpecProvider.Instance,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(notSyncedTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);

        Block? lastBestSuggestedBlock = null;

        notSyncedTree.NewBestSuggestedBlock += (sender, args) =>
        {
            lastBestSuggestedBlock = args.Block;
        };

        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(DownloaderOptions.Process | DownloaderOptions.WithBodies | DownloaderOptions.WithReceipts), CancellationToken.None);

        lastBestSuggestedBlock.Hash.Should().Be(lastHeader.Hash);
        lastBestSuggestedBlock.TotalDifficulty.Should().NotBeEquivalentTo(UInt256.Zero);
    }

    private BlockDownloader CreateMergeBlockDownloader(Context ctx)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        MemDb metadataDb = new MemDb();
        var testSpecProvider = new TestSpecProvider(London.Instance);
        testSpecProvider.TerminalTotalDifficulty = 0;
        PoSSwitcher posSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "0" }, new SyncConfig(), metadataDb, blockTree,
            testSpecProvider, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), metadataDb, blockTree, LimboLogs.Instance);
        InMemoryReceiptStorage receiptStorage = new();

        BlockCacheService blockCacheService = new();

        return new MergeBlockDownloader(
            posSwitcher,
            beaconPivot,
            ctx.Feed,
            ctx.PeerPool,
            ctx.BlockTree,
            Always.Valid,
            Always.Valid,
            NullSyncReport.Instance,
            receiptStorage,
            testSpecProvider,
            CreateMergePeerChoiceStrategy(posSwitcher, beaconPivot),
            new ChainLevelHelper(blockTree, beaconPivot, new SyncConfig(), LimboLogs.Instance),
            Substitute.For<ISyncProgressResolver>(),
            LimboLogs.Instance);
    }

    private IBetterPeerStrategy CreateMergePeerChoiceStrategy(IPoSSwitcher poSSwitcher, IBeaconPivot beaconPivot)
    {
        TotalDifficultyBetterPeerStrategy preMergePeerStrategy = new(LimboLogs.Instance);
        return new MergeBetterPeerStrategy(preMergePeerStrategy, poSSwitcher, beaconPivot, LimboLogs.Instance);
    }
}

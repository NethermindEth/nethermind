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
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(32L, DownloaderOptions.Process, 32, 32)]
    [TestCase(32L, DownloaderOptions.Process, 32, 29)]
    [TestCase(32L, DownloaderOptions.WithReceipts | DownloaderOptions.MoveToMain, 0, 32)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.WithReceipts | DownloaderOptions.MoveToMain, 32, 32)]
    [TestCase(SyncBatchSize.Max * 8, DownloaderOptions.Process, 32, 32)]
    public async Task Merge_Happy_path(long headNumber, int options, int threshold, long insertedBeaconBlocks)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(16)
            .InsertHeaders(4, 16)
            .InsertBeaconBlocks(16, insertedBeaconBlocks, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;
        Context ctx = new(notSyncedTree);
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.WithReceipts;
        InMemoryReceiptStorage receiptStorage = new();
        MergeConfig mergeConfig = new() { Enabled = true };
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        IBlockCacheService blockCacheService = new BlockCacheService();
        PoSSwitcher posSwitcher = new(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "0" }, new SyncConfig(), metadataDb, notSyncedTree,
            RopstenSpecProvider.Instance, blockCacheService, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), mergeConfig, metadataDb, notSyncedTree,
            new PeerRefresher(Substitute.For<ISyncPeerPool>()), LimboLogs.Instance);
        beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));
        MergeBlockDownloader downloader = new(posSwitcher, beaconPivot, ctx.Feed, ctx.PeerPool, notSyncedTree,
            Always.Valid, Always.Valid, NullSyncReport.Instance, receiptStorage, RopstenSpecProvider.Instance,
            CreateMergePeerChoiceStrategy(posSwitcher), new ChainLevelHelper(notSyncedTree, new SyncConfig(), LimboLogs.Instance),
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
    }

    [TestCase(32L, DownloaderOptions.MoveToMain, 32, false)]
    [TestCase(32L, DownloaderOptions.MoveToMain, 32, true)]
    public async Task Can_reach_terminal_block(long headNumber, int options, int threshold, bool withBeaconPivot)
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, (int)headNumber + 1)
            .InsertBeaconPivot(16)
            .InsertHeaders(4, 16)
            .InsertBeaconBlocks(16, headNumber, BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder.TotalDifficultyMode.Null);
        BlockTree notSyncedTree = blockTrees.NotSyncedTree;
        BlockTree syncedTree = blockTrees.SyncedTree;
        Context ctx = new(notSyncedTree);
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        InMemoryReceiptStorage receiptStorage = new();
        MergeConfig mergeConfig = new() { Enabled = true };
        MemDb metadataDb = blockTrees.NotSyncedTreeBuilder.MetadataDb;
        IBlockCacheService blockCacheService = new BlockCacheService();
        PoSSwitcher posSwitcher = new(new MergeConfig() { Enabled = true, TerminalTotalDifficulty = "10000000" }, new SyncConfig(), metadataDb, notSyncedTree,
            RopstenSpecProvider.Instance, blockCacheService, LimboLogs.Instance);
        BeaconPivot beaconPivot = new(new SyncConfig(), mergeConfig, metadataDb, notSyncedTree,
            new PeerRefresher(Substitute.For<ISyncPeerPool>()), LimboLogs.Instance);
        if (withBeaconPivot)
            beaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));
        MergeBlockDownloader downloader = new(posSwitcher, beaconPivot, ctx.Feed, ctx.PeerPool, notSyncedTree,
            Always.Valid, Always.Valid, NullSyncReport.Instance, receiptStorage, RopstenSpecProvider.Instance,
            CreateMergePeerChoiceStrategy(posSwitcher), new ChainLevelHelper(notSyncedTree, new SyncConfig(), LimboLogs.Instance),
            LimboLogs.Instance);

        SyncPeerMock syncPeer = new(syncedTree, false,  Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        Assert.True(posSwitcher.HasEverReachedTerminalBlock());
    }
    
    
    
    private IBetterPeerStrategy CreateMergePeerChoiceStrategy(IPoSSwitcher poSSwitcher)
    {
        ISyncProgressResolver syncProgressResolver = Substitute.For<ISyncProgressResolver>();
        TotalDifficultyBasedBetterPeerStrategy preMergePeerStrategy = new(syncProgressResolver, LimboLogs.Instance);
        return new MergeBetterPeerStrategy(preMergePeerStrategy, syncProgressResolver, poSSwitcher, LimboLogs.Instance);
    }
}

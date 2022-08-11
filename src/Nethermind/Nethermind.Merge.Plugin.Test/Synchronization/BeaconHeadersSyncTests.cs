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
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

[TestFixture]
public class BeaconHeadersSyncTests
{
    private class Context
    {
        public IBlockTree BlockTree;
        public ActivatedSyncFeed<HeadersSyncBatch> Feed;
        public IBeaconPivot BeaconPivot;
        public BeaconSync BeaconSync;

        private readonly IMergeConfig _mergeConfig;
        private readonly ISyncConfig _syncConfig;
        private readonly IDb _metadataDb;

        public Context(
            IBlockTree? blockTree = null,
            ISyncConfig? syncConfig = null,
            IBeaconPivot? beaconPivot = null,
            IDb? metadataDb = null,
            IMergeConfig? mergeConfig = null)
        {
            if (blockTree == null)
            {
                IDb blockInfoDb = new MemDb();
                Block genesis = Build.A.Block.Genesis.TestObject;
                BlockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
                BlockTree.SuggestBlock(genesis);
            }
            else
            {
                BlockTree = blockTree;
            }

            ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
            ISyncReport report = Substitute.For<ISyncReport>();
            MeasuredProgress measuredProgress = new MeasuredProgress();
            report.BeaconHeaders.Returns(measuredProgress);
            report.HeadersInQueue.Returns(measuredProgress);

            MemDb stateDb = new();
            _syncConfig = syncConfig ?? new SyncConfig();
            _mergeConfig = mergeConfig ?? new MergeConfig();
            _metadataDb = metadataDb ?? new MemDb();
            PoSSwitcher poSSwitcher = new(_mergeConfig, _syncConfig, _metadataDb, blockTree!,
                MainnetSpecProvider.Instance, LimboLogs.Instance);

            ProgressTracker progressTracker = new(BlockTree, stateDb, LimboLogs.Instance);

            SyncProgressResolver syncProgressResolver = new(
                BlockTree,
                NullReceiptStorage.Instance,
                stateDb,
                new TrieStore(stateDb, LimboLogs.Instance),
                progressTracker,
                _syncConfig,
                LimboLogs.Instance);
            TotalDifficultyBetterPeerStrategy bestPeerStrategy = new (LimboLogs.Instance);
            BeaconPivot = beaconPivot ?? new BeaconPivot(_syncConfig, _metadataDb, BlockTree, LimboLogs.Instance);
            BeaconSync = new(BeaconPivot, BlockTree, _syncConfig,  new BlockCacheService(), LimboLogs.Instance);
            ISyncModeSelector selector = new MultiSyncModeSelector(syncProgressResolver, peerPool, _syncConfig, BeaconSync, bestPeerStrategy, LimboLogs.Instance);
            Feed = new BeaconHeadersSyncFeed(poSSwitcher, selector, blockTree, peerPool, _syncConfig, report, BeaconPivot, _mergeConfig,
                new NoopInvalidChainTracker(), LimboLogs.Instance);
        }
    }

    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        IDbProvider memDbProvider = await TestMemDbProvider.InitAsync();
        BlockTree blockTree = new(memDbProvider, new ChainLevelInfoRepository(memDbProvider.BlockInfosDb),
            MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };
        PoSSwitcher poSSwitcher = new(new MergeConfig(), syncConfig, memDbProvider.MetadataDb, blockTree!,
            MainnetSpecProvider.Instance, LimboLogs.Instance);
        IBeaconPivot pivot = PreparePivot(2000, syncConfig, blockTree);
        BeaconHeadersSyncFeed feed = new(poSSwitcher, Substitute.For<ISyncModeSelector>(), blockTree,
            Substitute.For<ISyncPeerPool>(), syncConfig, Substitute.For<ISyncReport>(),
            pivot, new MergeConfig() {Enabled = true}, new NoopInvalidChainTracker(), LimboLogs.Instance);
        feed.InitializeFeed();
        for (int i = 0; i < 6; i++)
        {
            await feed.PrepareRequest();
        }

        HeadersSyncBatch? result = await feed.PrepareRequest();
        result.Should().BeNull();
    }

    [Test]
    public async Task Finishes_when_all_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(2000).TestObject);
        ISyncReport report = Substitute.For<ISyncReport>();
        report.HeadersInQueue.Returns(new MeasuredProgress());
        MeasuredProgress measuredProgress = new ();
        report.BeaconHeaders.Returns(measuredProgress);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
         };
        PoSSwitcher poSSwitcher = new(new MergeConfig(), syncConfig, new MemDb(), blockTree!,
            MainnetSpecProvider.Instance, LimboLogs.Instance);
        IBeaconPivot pivot = PreparePivot(2000, syncConfig, blockTree);
        BeaconHeadersSyncFeed feed = new (poSSwitcher, Substitute.For<ISyncModeSelector>(), blockTree,
            Substitute.For<ISyncPeerPool>(), syncConfig, report, pivot, new MergeConfig() {Enabled = true},
            new NoopInvalidChainTracker(), LimboLogs.Instance);
        feed.InitializeFeed();
        for (int i = 0; i < 6; i++)
        {
            await feed.PrepareRequest();
        }
        blockTree.LowestInsertedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(1001).TestObject);
        HeadersSyncBatch? result = await feed.PrepareRequest();
        result.Should().BeNull();
        feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        measuredProgress.CurrentValue.Should().Be(999);
    }

    [Test]
    public async Task Feed_able_to_sync_when_new_pivot_is_set()
    {
        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(1000).TestObject;
        Block genesisBlock = syncedBlockTree.FindBlock(syncedBlockTree.GenesisHash, BlockTreeLookupOptions.None)!;
        BlockTree blockTree = Build.A.BlockTree().TestObject;
        blockTree.SuggestBlock(genesisBlock);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "500",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000000" // default difficulty in block tree builder
        };
        BlockHeader? pivotHeader = syncedBlockTree.FindHeader(700, BlockTreeLookupOptions.None);
        IBeaconPivot pivot = PreparePivot(700, syncConfig, blockTree, pivotHeader);
        Context ctx = new (blockTree, syncConfig, pivot);

        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 0, 501);

        // move best pointers forward as proxy for chain merge
        Block highestBlock = syncedBlockTree.FindBlock(700, BlockTreeLookupOptions.None)!;
        blockTree.Insert(highestBlock, BlockTreeInsertBlockOptions.SaveHeader);

        pivot.EnsurePivot(syncedBlockTree.FindHeader(900, BlockTreeLookupOptions.None));
        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 700, 701);

        highestBlock = syncedBlockTree.FindBlock(900, BlockTreeLookupOptions.None)!;
        blockTree.Insert(highestBlock, BlockTreeInsertBlockOptions.SaveHeader);
        pivot.EnsurePivot(syncedBlockTree.FindHeader(999, BlockTreeLookupOptions.None));
        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 900, 901);
    }

    [Test]
    public async Task Feed_able_to_connect_to_existing_chain_through_block_hash()
    {
        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;
        Block genesisBlock = syncedBlockTree.FindBlock(syncedBlockTree.GenesisHash, BlockTreeLookupOptions.None)!;
        BlockTree blockTree = Build.A.BlockTree().TestObject;
        blockTree.SuggestBlock(genesisBlock);
        Block? firstBlock = syncedBlockTree.FindBlock(1, BlockTreeLookupOptions.None);
        blockTree.SuggestBlock(firstBlock);
        BlockHeader? pivotHeader = syncedBlockTree.FindHeader(500, BlockTreeLookupOptions.None);
        IBeaconPivot pivot = PreparePivot(500, new SyncConfig(), blockTree, pivotHeader);
        Context ctx = new (blockTree, new SyncConfig(), pivot);
        // fork in chain
        Block parent = firstBlock;
        for (int i = 0; i < 5; i++)
        {
            Block block = Build.A.Block.WithParent(parent).WithNonce(1).TestObject;
            blockTree.SuggestBlock(block);
            parent = block;
        }

        ctx.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        blockTree.BestKnownNumber.Should().Be(6);
        BuildHeadersSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 2);
        HeadersSyncBatch result = await ctx.Feed.PrepareRequest();
        result.Should().BeNull();
        blockTree.BestKnownNumber.Should().Be(6);
        ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        ctx.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
    }

    private async void BuildAndProcessHeaderSyncBatches(
        Context ctx,
        BlockTree blockTree,
        BlockTree syncedBlockTree,
        IBeaconPivot pivot,
        long bestPointer,
        long endLowestBeaconHeader)
    {
        ctx.BeaconSync.ShouldBeInBeaconHeaders().Should().BeTrue();
        blockTree.BestKnownNumber.Should().Be(bestPointer);
        BlockHeader? startBestHeader = syncedBlockTree.FindHeader(bestPointer, BlockTreeLookupOptions.None);
        blockTree.BestSuggestedHeader.Should().BeEquivalentTo(startBestHeader);
        blockTree.LowestInsertedBeaconHeader.Should().BeEquivalentTo(syncedBlockTree.FindHeader(pivot.PivotNumber, BlockTreeLookupOptions.None));

        BuildHeadersSyncBatches(ctx, blockTree, syncedBlockTree, pivot, endLowestBeaconHeader);

        HeadersSyncBatch result = await ctx.Feed.PrepareRequest();
        result.Should().BeNull();
        // check headers are inserted into block tree during sync
        blockTree.FindHeader(pivot.PivotNumber - 1, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Should().NotBeNull();
        blockTree.LowestInsertedBeaconHeader?.Hash.Should().BeEquivalentTo(syncedBlockTree.FindHeader(endLowestBeaconHeader, BlockTreeLookupOptions.None)?.Hash);
        blockTree.BestKnownNumber.Should().Be(bestPointer);
        blockTree.BestSuggestedHeader.Should().BeEquivalentTo(startBestHeader);
        ctx.Feed.CurrentState.Should().Be(SyncFeedState.Dormant);
        ctx.BeaconSync.ShouldBeInBeaconHeaders().Should().BeFalse();
    }

    private async void BuildHeadersSyncBatches(
        Context ctx,
        BlockTree blockTree,
        BlockTree syncedBlockTree,
        IBeaconPivot pivot,
        long endLowestBeaconHeader)
    {
        ctx.Feed.InitializeFeed();
        long lowestHeaderNumber = pivot.PivotNumber;
        while (lowestHeaderNumber > endLowestBeaconHeader)
        {
            HeadersSyncBatch batch = await ctx.Feed.PrepareRequest();
            batch.Should().NotBeNull();
            BuildHeadersSyncBatchResponse(batch, syncedBlockTree);
            ctx.Feed.HandleResponse(batch);
            lowestHeaderNumber = lowestHeaderNumber - batch.RequestSize < endLowestBeaconHeader
                ? endLowestBeaconHeader
                : lowestHeaderNumber - batch.RequestSize;

            BlockHeader? lowestHeader = syncedBlockTree.FindHeader(lowestHeaderNumber, BlockTreeLookupOptions.None);
            blockTree.LowestInsertedBeaconHeader?.Hash.Should().BeEquivalentTo(lowestHeader?.Hash);
        }
    }

    private void BuildHeadersSyncBatchResponse(HeadersSyncBatch batch, IBlockTree blockTree)
    {
        batch.MarkSent();
        BlockHeader? startHeader = blockTree.FindHeader(batch.StartNumber);
        if (startHeader == null)
        {
            return;
        }

        BlockHeader[] headers = blockTree.FindHeaders(startHeader.Hash!, batch.RequestSize, 0, true);
        batch.Response = headers;
    }

    private IBeaconPivot PreparePivot(long blockNumber, ISyncConfig syncConfig, IBlockTree blockTree, BlockHeader? pivotHeader = null)
    {
        IBeaconPivot pivot = new BeaconPivot(syncConfig, new MemDb(), blockTree, LimboLogs.Instance);
        pivot.EnsurePivot(pivotHeader ?? Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        return pivot;
    }
}

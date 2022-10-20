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
using Nethermind.Merge.Plugin.InvalidChainTracker;
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
        private IBlockTree _blockTree;
        public IBlockTree BlockTree
        {
            get
            {
                if (_blockTree == null)
                {
                    IDb blockInfoDb = new MemDb();
                    Block genesis = Build.A.Block.Genesis.TestObject;
                    _blockTree = new BlockTree(new MemDb(), new MemDb(), blockInfoDb, new ChainLevelInfoRepository(blockInfoDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
                    _blockTree.SuggestBlock(genesis);
                }

                return _blockTree;
            }
            set => _blockTree = value;
        }

        private IBeaconPivot? _beaconPivot;
        public IBeaconPivot BeaconPivot
        {
            get => _beaconPivot ??= new BeaconPivot(SyncConfig, MetadataDb, BlockTree, LimboLogs.Instance);
            set => _beaconPivot = value;
        }

        private BeaconSync? _beaconSync;
        public BeaconSync BeaconSync => _beaconSync ??= new(BeaconPivot, BlockTree, SyncConfig, BlockCacheService, LimboLogs.Instance);

        private IDb? _metadataDb;
        public IDb MetadataDb => _metadataDb ??= new MemDb();

        private PoSSwitcher? _poSSwitcher;
        public PoSSwitcher PoSSwitcher => _poSSwitcher ??= new(MergeConfig, SyncConfig, MetadataDb, BlockTree,
                MainnetSpecProvider.Instance, LimboLogs.Instance);

        private IInvalidChainTracker? _invalidChainTracker;

        public IInvalidChainTracker InvalidChainTracker
        {
            get => _invalidChainTracker ??= new NoopInvalidChainTracker();
            set => _invalidChainTracker = value;
        }

        private BeaconHeadersSyncFeed? _feed;
        public BeaconHeadersSyncFeed Feed => _feed ??= new BeaconHeadersSyncFeed(
            PoSSwitcher,
            Selector,
            BlockTree,
            PeerPool,
            SyncConfig,
            Report,
            BeaconPivot,
            MergeConfig,
            InvalidChainTracker,
            LimboLogs.Instance
        );

        private MultiSyncModeSelector? _selector;
        public MultiSyncModeSelector Selector
        {
            get
            {
                if (_selector == null)
                {
                    MemDb stateDb = new();
                    ProgressTracker progressTracker = new(BlockTree, stateDb, LimboLogs.Instance);
                    SyncProgressResolver syncProgressResolver = new(
                        BlockTree,
                        NullReceiptStorage.Instance,
                        stateDb,
                        new TrieStore(stateDb, LimboLogs.Instance),
                        progressTracker,
                        SyncConfig,
                        LimboLogs.Instance);
                    TotalDifficultyBetterPeerStrategy bestPeerStrategy = new(LimboLogs.Instance);
                    _selector = new MultiSyncModeSelector(syncProgressResolver, PeerPool, SyncConfig, BeaconSync,
                        bestPeerStrategy, LimboLogs.Instance);
                }

                return _selector;
            }
        }

        private ISyncPeerPool? _peerPool;
        public ISyncPeerPool PeerPool => _peerPool ??= Substitute.For<ISyncPeerPool>();

        private ISyncConfig? _syncConfig;
        public ISyncConfig SyncConfig
        {
            get => _syncConfig ??= new SyncConfig();
            set => _syncConfig = value;
        }

        private ISyncReport? _report;
        public ISyncReport Report
        {
            get
            {
                if (_report == null)
                {
                    _report = Substitute.For<ISyncReport>();
                    MeasuredProgress measuredProgress = new MeasuredProgress();
                    Report.BeaconHeaders.Returns(measuredProgress);
                    Report.HeadersInQueue.Returns(measuredProgress);
                }

                return _report;
            }
            set => _report = value;
        }

        private IMergeConfig? _mergeConfig;
        public IMergeConfig MergeConfig => _mergeConfig ??= new MergeConfig();

        private IBlockCacheService? _blockCacheService;
        public IBlockCacheService BlockCacheService => _blockCacheService ??= new BlockCacheService();
    }

    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        Context ctx = new()
        {
            SyncConfig = new SyncConfig
            {
                FastSync = true,
                FastBlocks = true,
                PivotNumber = "1000",
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            MergeConfig = { }
        };
        ctx.BeaconPivot = PreparePivot(2000, ctx.SyncConfig, ctx.BlockTree);
        BeaconHeadersSyncFeed feed = ctx.Feed;
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
        MeasuredProgress measuredProgress = new();
        report.BeaconHeaders.Returns(measuredProgress);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            FastBlocks = true,
            PivotNumber = "1000",
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000"
        };

        Context ctx = new()
        {
            BlockTree = blockTree,
            Report = report,
            SyncConfig = syncConfig,
            MergeConfig = { }
        };
        ctx.BeaconPivot = PreparePivot(2000, syncConfig, blockTree);
        BeaconHeadersSyncFeed feed = ctx.Feed;
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

        Context ctx = new() { BlockTree = blockTree, SyncConfig = syncConfig, BeaconPivot = pivot };

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
        Context ctx = new() { BlockTree = blockTree, BeaconPivot = pivot };

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

    [Test]
    public void Feed_connect_invalid_chain()
    {
        Context ctx = new();
        IInvalidChainTracker invalidChainTracker = new InvalidChainTracker.InvalidChainTracker(ctx.PoSSwitcher,
            ctx.BlockTree, ctx.BlockCacheService, LimboLogs.Instance);
        ctx.InvalidChainTracker = invalidChainTracker;

        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(100).TestObject;
        HeadersSyncBatch batch = new HeadersSyncBatch();
        batch.RequestSize = 100;
        batch.Response = syncedBlockTree.FindHeaders(syncedBlockTree.GenesisHash, 100, 0, false);
        ctx.Feed.HandleResponse(batch);

        Keccak lastHeader = syncedBlockTree.FindHeader(99, BlockTreeLookupOptions.None).Hash;
        Keccak headerToInvalidate = syncedBlockTree.FindHeader(10, BlockTreeLookupOptions.None).Hash;
        Keccak lastValidHeader = syncedBlockTree.FindHeader(9, BlockTreeLookupOptions.None).Hash;
        invalidChainTracker.OnInvalidBlock(headerToInvalidate, lastValidHeader);
        invalidChainTracker.IsOnKnownInvalidChain(lastHeader, out Keccak storedLastValidHash).Should().BeTrue();
        storedLastValidHash.Should().Be(lastValidHeader);
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

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.State.Repositories;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

[TestFixture]
public class BeaconHeadersSyncTests
{
    private class Context
    {
        private BlockTreeBuilder? _blockTreeBuilder;
        private BlockTreeBuilder BlockTreeBuilder => _blockTreeBuilder ??= Build.A.BlockTree()
            .WithSyncConfig(SyncConfig)
            .WithoutSettingHead;

        public IChainLevelInfoRepository ChainLevelInfoRepository => BlockTreeBuilder.ChainLevelInfoRepository;
        public IHeaderStore HeaderStore => BlockTreeBuilder.HeaderStore;

        private IBlockTree? _blockTree;
        public IBlockTree BlockTree
        {
            get
            {
                if (_blockTree is null)
                {
                    Block genesis = Build.A.Block.Genesis.TestObject;
                    _blockTree = BlockTreeBuilder.TestObject;
                    _blockTree.SuggestBlock(genesis);
                    _blockTree.TryUpdateMainChain(genesis.Header, true, preloadedBlocks: new[] { genesis }); // MSMS do validity check on this
                }

                return _blockTree;
            }
            set => _blockTree = value;
        }

        private IBeaconPivot? _beaconPivot;
        public IBeaconPivot BeaconPivot
        {
            get => _beaconPivot ??= new BeaconPivot(SyncConfig, MetadataDb, BlockTree, PoSSwitcher, LimboLogs.Instance);
            set => _beaconPivot = value;
        }

        private BeaconSync? _beaconSync;
        public BeaconSync BeaconSync => _beaconSync ??= new(BeaconPivot, BlockTree, SyncConfig, BlockCacheService, PoSSwitcher, LimboLogs.Instance);

        private IDb? _metadataDb;
        public IDb MetadataDb => _metadataDb ??= new MemDb();

        private PoSSwitcher? _poSSwitcher;
        public PoSSwitcher PoSSwitcher => _poSSwitcher ??= new(MergeConfig, SyncConfig, MetadataDb, BlockTree,
                MainnetSpecProvider.Instance, new ChainSpec(), LimboLogs.Instance);

        private IInvalidChainTracker? _invalidChainTracker;

        public IInvalidChainTracker InvalidChainTracker
        {
            get => _invalidChainTracker ??= new NoopInvalidChainTracker();
            set => _invalidChainTracker = value;
        }

        private BeaconHeadersSyncFeed? _feed;
        public BeaconHeadersSyncFeed Feed => _feed ??= new BeaconHeadersSyncFeed(
            PoSSwitcher,
            BlockTree,
            PeerPool,
            SyncConfig,
            Report,
            BeaconPivot,
            InvalidChainTracker,
            LimboLogs.Instance,
            ChainLevelInfoRepository,
            HeaderStore
        );

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
                if (_report is null)
                {
                    _report = Substitute.For<ISyncReport>();
                    ProgressLogger progressLogger = new("", LimboLogs.Instance);
                    Report.BeaconHeaders.Returns(progressLogger);
                }

                return _report;
            }
            set => _report = value;
        }

        private IMergeConfig? _mergeConfig;
        public IMergeConfig MergeConfig => _mergeConfig ??= new MergeConfig();

        private IBlockCacheService? _blockCacheService;
        public IBlockCacheService BlockCacheService => _blockCacheService ??= new BlockCacheService();

        private IBlockTree? _remoteBlockTree;
        public IBlockTree RemoteBlockTree
        {
            get
            {
                return _remoteBlockTree ??= Build.A.BlockTree().TestObject;
            }
            set => _remoteBlockTree = value;
        }

        public void SetupRemoteBlockTreeOfLength(int chainLength) =>
            RemoteBlockTree = Build.A.BlockTree().OfChainLength(chainLength).TestObject;
    }

    [Test]
    public async Task Can_keep_returning_nulls_after_all_batches_were_prepared()
    {
        Context ctx = new()
        {
            SyncConfig = new SyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
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

        using HeadersSyncBatch? result = await feed.PrepareRequest();
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Finishes_when_all_downloaded()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.LowestInsertedBeaconHeader.Returns(Build.A.BlockHeader.WithNumber(2000).TestObject);
        blockTree.SyncPivot.Returns((1000UL, Keccak.Zero));
        ISyncReport report = Substitute.For<ISyncReport>();
        ProgressLogger progressLogger = new("", LimboLogs.Instance);
        report.BeaconHeaders.Returns(progressLogger);
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            PivotNumber = 1000,
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
        using HeadersSyncBatch? result = await feed.PrepareRequest();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));
            Assert.That(progressLogger.CurrentValue, Is.EqualTo(999));
        }
    }

    [Test]
    public void Feed_able_to_sync_when_new_pivot_is_set()
    {
        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(1000).TestObject;
        Block genesisBlock = syncedBlockTree.FindBlock(syncedBlockTree.GenesisHash, BlockTreeLookupOptions.None)!;
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            PivotNumber = 500,
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "1000000" // default difficulty in block tree builder
        };
        BlockTree blockTree = Build.A.BlockTree().WithSyncConfig(syncConfig).TestObject;
        blockTree.SuggestBlock(genesisBlock);
        BlockHeader? pivotHeader = syncedBlockTree.FindHeader(700, BlockTreeLookupOptions.None);
        IBeaconPivot pivot = PreparePivot(700, syncConfig, blockTree, pivotHeader);

        Context ctx = new() { BlockTree = blockTree, SyncConfig = syncConfig, BeaconPivot = pivot };

        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 0UL, 501UL);

        // move best pointers forward as proxy for chain merge
        Block highestBlock = syncedBlockTree.FindBlock(700, BlockTreeLookupOptions.None)!;
        blockTree.Insert(highestBlock, BlockTreeInsertBlockOptions.SaveHeader);

        pivot.EnsurePivot(syncedBlockTree.FindHeader(900, BlockTreeLookupOptions.None));
        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 700UL, 701UL);

        highestBlock = syncedBlockTree.FindBlock(900, BlockTreeLookupOptions.None)!;
        blockTree.Insert(highestBlock, BlockTreeInsertBlockOptions.SaveHeader);
        pivot.EnsurePivot(syncedBlockTree.FindHeader(999, BlockTreeLookupOptions.None));
        BuildAndProcessHeaderSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 900UL, 901UL);
    }

    [Test]
    public async Task Feed_able_to_connect_to_existing_chain_through_block_hash()
    {
        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(600).TestObject;
        Block genesisBlock = syncedBlockTree.FindBlock(syncedBlockTree.GenesisHash, BlockTreeLookupOptions.None)!;
        BlockTree blockTree = Build.A.BlockTree().TestObject;
        blockTree.SuggestBlock(genesisBlock);
        Block? firstBlock = syncedBlockTree.FindBlock(1, BlockTreeLookupOptions.None)!;
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

        Assert.That(ctx.BeaconSync.ShouldBeInBeaconHeaders(), Is.True);
        Assert.That(blockTree.BestKnownNumber, Is.EqualTo(6));
        BuildHeadersSyncBatches(ctx, blockTree, syncedBlockTree, pivot, 2);
        using HeadersSyncBatch? result = await ctx.Feed.PrepareRequest();
        Assert.That(result, Is.Null);
        Assert.That(blockTree.BestKnownNumber, Is.EqualTo(6));
        Assert.That(ctx.Feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));
        Assert.That(ctx.BeaconSync.ShouldBeInBeaconHeaders(), Is.False);
    }

    [Test]
    public void Feed_connect_invalid_chain()
    {
        Context ctx = new();
        IInvalidChainTracker invalidChainTracker = new InvalidChainTracker.InvalidChainTracker(ctx.PoSSwitcher,
            ctx.BlockTree, ctx.BlockCacheService, LimboLogs.Instance);
        ctx.InvalidChainTracker = invalidChainTracker;

        BlockTree syncedBlockTree = Build.A.BlockTree().OfChainLength(100, splitVariant: (int)BlockHeaderBuilder.DefaultDifficulty).TestObject;
        ctx.BeaconPivot = PreparePivot(99, new SyncConfig(), ctx.BlockTree,
            syncedBlockTree.FindHeader(99, BlockTreeLookupOptions.None));
        ctx.Feed.InitializeFeed();
        using HeadersSyncBatch batch = ctx.Feed.PrepareRequest().Result!;
        batch.Response = syncedBlockTree.FindHeaders(syncedBlockTree.FindHeader(batch.StartNumber, BlockTreeLookupOptions.None)!.Hash, batch.RequestSize, 0, false)!;
        ctx.Feed.HandleResponse(batch);

        Hash256 lastHeader = syncedBlockTree.FindHeader(batch.EndNumber, BlockTreeLookupOptions.None)!.GetOrCalculateHash();
        Hash256 headerToInvalidate = syncedBlockTree.FindHeader(batch.StartNumber + 10, BlockTreeLookupOptions.None)!.GetOrCalculateHash();
        Hash256 lastValidHeader = syncedBlockTree.FindHeader(batch.StartNumber + 9, BlockTreeLookupOptions.None)!.GetOrCalculateHash();
        invalidChainTracker.OnInvalidBlock(headerToInvalidate, lastValidHeader);
        Assert.That(invalidChainTracker.IsOnKnownInvalidChain(lastHeader, out Hash256? storedLastValidHash), Is.True);
        Assert.That(storedLastValidHash, Is.EqualTo(lastValidHeader));
    }

    [Test]
    public async Task Does_not_request_headers_when_destination_advances_past_lowest_requested()
    {
        // PivotDestinationNumber rising above _lowestRequestedHeaderNumber mid-sync must not produce
        // a batch with negative RequestSize.
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.SyncPivot.Returns((1000UL, Keccak.Zero));
        blockTree.LowestInsertedBeaconHeader.Returns((BlockHeader?)null);

        IBeaconPivot beaconPivot = Substitute.For<IBeaconPivot>();
        beaconPivot.PivotNumber.Returns(2000UL);
        beaconPivot.PivotHash.Returns(TestItem.KeccakA);
        beaconPivot.PivotParentHash.Returns(TestItem.KeccakB);
        beaconPivot.PivotDestinationNumber.Returns(1100UL);

        Context ctx = new()
        {
            BlockTree = blockTree,
            SyncConfig = new SyncConfig
            {
                FastSync = true,
                PivotNumber = 1000,
                PivotHash = Keccak.Zero.ToString(),
                PivotTotalDifficulty = "1000"
            },
            BeaconPivot = beaconPivot,
        };
        BeaconHeadersSyncFeed feed = ctx.Feed;
        feed.InitializeFeed();

        using HeadersSyncBatch? first = await feed.PrepareRequest();
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.RequestSize, Is.GreaterThan(0));

        // Simulate Head advancing so that PivotDestinationNumber moves above _lowestRequestedHeaderNumber.
        beaconPivot.PivotDestinationNumber.Returns(first.StartNumber + 10);

        using HeadersSyncBatch? second = await feed.PrepareRequest();
        Assert.That(second, Is.Null);
    }

    [Test]
    public async Task When_pivot_changed_during_header_sync_after_chain_merged__do_not_return_null_request()
    {
        ISyncConfig syncConfig = new SyncConfig
        {
            FastSync = true,
            PivotNumber = 0,
            PivotHash = Keccak.Zero.ToString(),
            PivotTotalDifficulty = "0"
        };

        int chainLength = 111;
        uint pivotNumber = 100;

        Context ctx = new()
        {
            SyncConfig = syncConfig,
        };
        ctx.SetupRemoteBlockTreeOfLength(chainLength);

        ctx.BeaconPivot.EnsurePivot(ctx.RemoteBlockTree.FindHeader(pivotNumber));

        ctx.Feed.InitializeFeed();

        // First batch, should be enough to merge chain
        HeadersSyncBatch? request = await ctx.Feed.PrepareRequest();
        Assert.That(request!, Is.Not.Null);
        request!.Response = TestRange.ULongRange(request.StartNumber, request.RequestSize)
            .Select(blockNumber => ctx.RemoteBlockTree.FindHeader(blockNumber)!)
            .ToPooledList(request.RequestSize);

        ctx.Feed.HandleResponse(request);

        // Ensure pivot happens which reset lowest inserted beacon header further ahead.
        ctx.BeaconPivot.EnsurePivot(ctx.RemoteBlockTree.FindHeader(pivotNumber + 10));
        Assert.That(ctx.BeaconSync.IsBeaconSyncHeadersFinished(), Is.False);
        request.Dispose();

        // The sync feed must adapt to this
        request = await ctx.Feed.PrepareRequest();
        Assert.That(request, Is.Not.Null);

        // We respond it again
        request!.Response = TestRange.ULongRange(request.StartNumber, request.RequestSize)
            .Select(blockNumber => ctx.RemoteBlockTree.FindHeader(blockNumber)!)
            .ToPooledList(request.RequestSize);
        ctx.Feed.HandleResponse(request);
        request.Dispose();

        // It should complete successfully
        Assert.That(ctx.BeaconSync.IsBeaconSyncHeadersFinished(), Is.True);
        request = await ctx.Feed.PrepareRequest();
        Assert.That(request, Is.Null);
    }

    private async void BuildAndProcessHeaderSyncBatches(
        Context ctx,
        BlockTree blockTree,
        BlockTree syncedBlockTree,
        IBeaconPivot pivot,
        ulong bestPointer,
        ulong endLowestBeaconHeader)
    {
        BlockHeader? startBestHeader = syncedBlockTree.FindHeader(bestPointer, BlockTreeLookupOptions.None);
        BlockHeader? pivotHeader = syncedBlockTree.FindHeader(pivot.PivotNumber, BlockTreeLookupOptions.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ctx.BeaconSync.ShouldBeInBeaconHeaders(), Is.True);
            Assert.That(blockTree.BestKnownNumber, Is.EqualTo(bestPointer));
            Assert.That(blockTree.BestSuggestedHeader?.Hash, Is.EqualTo(startBestHeader?.Hash));
            Assert.That(blockTree.BestSuggestedHeader?.Number, Is.EqualTo(startBestHeader?.Number));
            Assert.That(blockTree.LowestInsertedBeaconHeader?.Hash, Is.EqualTo(pivotHeader?.Hash));
            Assert.That(blockTree.LowestInsertedBeaconHeader?.Number, Is.EqualTo(pivotHeader?.Number));
        }

        BuildHeadersSyncBatches(ctx, blockTree, syncedBlockTree, pivot, endLowestBeaconHeader);

        HeadersSyncBatch? result = await ctx.Feed.PrepareRequest();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            // check headers are inserted into block tree during sync
            Assert.That(blockTree.FindHeader(pivot.PivotNumber - 1, BlockTreeLookupOptions.TotalDifficultyNotNeeded), Is.Not.Null);
            Assert.That(blockTree.LowestInsertedBeaconHeader?.Hash, Is.EqualTo(syncedBlockTree.FindHeader(endLowestBeaconHeader, BlockTreeLookupOptions.None)?.Hash));
            Assert.That(blockTree.BestKnownNumber, Is.EqualTo(bestPointer));
            Assert.That(blockTree.BestSuggestedHeader?.Hash, Is.EqualTo(startBestHeader?.Hash));
            Assert.That(blockTree.BestSuggestedHeader?.Number, Is.EqualTo(startBestHeader?.Number));
            Assert.That(ctx.Feed.CurrentState, Is.EqualTo(SyncFeedState.Dormant));
            Assert.That(ctx.BeaconSync.ShouldBeInBeaconHeaders(), Is.False);
        }
    }

    private async void BuildHeadersSyncBatches(
        Context ctx,
        BlockTree blockTree,
        BlockTree syncedBlockTree,
        IBeaconPivot pivot,
        ulong endLowestBeaconHeader)
    {
        ctx.Feed.InitializeFeed();
        ulong lowestHeaderNumber = pivot.PivotNumber;
        while (lowestHeaderNumber > endLowestBeaconHeader)
        {
            using HeadersSyncBatch? batch = await ctx.Feed.PrepareRequest();
            Assert.That(batch, Is.Not.Null);
            BuildHeadersSyncBatchResponse(batch, syncedBlockTree);
            ctx.Feed.HandleResponse(batch);
            lowestHeaderNumber = lowestHeaderNumber.SaturatingSub((ulong)batch!.RequestSize);
            if (lowestHeaderNumber < endLowestBeaconHeader) lowestHeaderNumber = endLowestBeaconHeader;

            BlockHeader? lowestHeader = syncedBlockTree.FindHeader(lowestHeaderNumber, BlockTreeLookupOptions.None);
            Assert.That(blockTree.LowestInsertedBeaconHeader?.Number, Is.EqualTo(lowestHeader?.Number));
            Assert.That(blockTree.LowestInsertedBeaconHeader?.Hash, Is.EqualTo(lowestHeader?.Hash));
        }
    }

    private static void BuildHeadersSyncBatchResponse(HeadersSyncBatch? batch, IBlockTree blockTree)
    {
        batch!.MarkSent();
        BlockHeader? startHeader = blockTree.FindHeader(batch.StartNumber);
        if (startHeader is null)
        {
            return;
        }

        using IOwnedReadOnlyList<BlockHeader?> headers = blockTree.FindHeaders(startHeader.Hash!, batch.RequestSize, 0, false);
        ArrayPoolList<BlockHeader> response = new(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            response.Add(headers[i]!);
        }

        batch.Response = response;
    }

    private static IBeaconPivot PreparePivot(ulong blockNumber, ISyncConfig syncConfig, IBlockTree blockTree, BlockHeader? pivotHeader = null)
    {
        IBeaconPivot pivot = new BeaconPivot(syncConfig, new MemDb(), blockTree, AlwaysPoS.Instance, LimboLogs.Instance);
        pivot.EnsurePivot(pivotHeader ?? Build.A.BlockHeader.WithNumber(blockNumber).TestObject);
        return pivot;
    }

}

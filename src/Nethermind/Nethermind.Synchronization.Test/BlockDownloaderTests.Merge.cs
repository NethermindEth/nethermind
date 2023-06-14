// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NSubstitute.ClearExtensions;
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
        BlockTree syncedTree = blockTrees.SyncedTree;
        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.WithReceipts;
        ctx.MergeConfig = new MergeConfig() { TerminalTotalDifficulty = "0" };
        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        BlockDownloader downloader = ctx.BlockDownloader;

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

        ctx.ReceiptStorage.Count.Should().Be(withReceipts ? receiptCount : 0);
        ctx.BeaconPivot.ProcessDestination?.Number.Should().Be(insertedBeaconBlocks);
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
        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        ctx.MergeConfig = new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" };
        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        BlockDownloader downloader = ctx.BlockDownloader;

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 16000000);
        PeerInfo peerInfo = new(syncPeer);
        await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);
        Assert.True(ctx.PosSwitcher.HasEverReachedTerminalBlock());
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

        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;

        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        ctx.MergeConfig = new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" };
        if (withBeaconPivot)
            ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(16, BlockTreeLookupOptions.None));

        BlockDownloader downloader = ctx.BlockDownloader;

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

        BlockTree syncedTree = blockTrees.SyncedTree;
        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None));
        ctx.BeaconPivot.ProcessDestination = blockTrees.SyncedTree.FindHeader(pivot, BlockTreeLookupOptions.None);

        BlockDownloader downloader = ctx.BlockDownloader;

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

        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;
        ctx.MergeConfig = new MergeConfig() { TerminalTotalDifficulty = $"{ttd}" };

        BlockHeader lastHeader = syncedTree.FindHeader(3, BlockTreeLookupOptions.None);
        // Because the FindHeader recalculated the TD.
        lastHeader.TotalDifficulty = 0;

        ctx.BeaconPivot.EnsurePivot(lastHeader);

        ISealValidator sealValidator = Substitute.For<ISealValidator>();
        sealValidator.ValidateSeal(Arg.Any<BlockHeader>(), Arg.Any<bool>()).Returns((info =>
        {
            BlockHeader header = (BlockHeader)info[0];
            // Simulate something calls find header on the header, causing the TD to get recalculated
            notSyncedTree.FindHeader(header.Hash, BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
            return true;
        }));
        ctx.SealValidator = sealValidator;

        BlockDownloader downloader = ctx.BlockDownloader;

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

    [Test]
    public async Task Does_not_deadlock_on_replace_peer()
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(0, 4)
            .InsertBeaconPivot(3);
        PostMergeContext ctx = new();
        ctx.MergeConfig = new MergeConfig() { TerminalTotalDifficulty = "0" };
        ctx.BlockTreeScenario = blockTrees;
        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(3, BlockTreeLookupOptions.None));

        ManualResetEventSlim chainLevelHelperBlocker = new ManualResetEventSlim(false);
        IChainLevelHelper chainLevelHelper = Substitute.For<IChainLevelHelper>();
        chainLevelHelper
            .When((clh) => clh.GetNextHeaders(Arg.Any<int>(), Arg.Any<int>()))
            .Do((args) =>
            {
                chainLevelHelperBlocker.Wait();
            });
        ctx.ChainLevelHelper = chainLevelHelper;

        IPeerAllocationStrategy peerAllocationStrategy = Substitute.For<IPeerAllocationStrategy>();

        // Setup a peer of any kind
        ISyncPeer syncPeer1 = Substitute.For<ISyncPeer>();
        syncPeer1.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 9999));

        // Setup so that first allocation goes to sync peer 1
        peerAllocationStrategy
            .Allocate(Arg.Any<PeerInfo?>(), Arg.Any<IEnumerable<PeerInfo>>(), Arg.Any<INodeStatsManager>(), Arg.Any<IBlockTree>())
            .Returns(new PeerInfo(syncPeer1));
        SyncPeerAllocation peerAllocation = new(peerAllocationStrategy, AllocationContexts.Blocks);
        peerAllocation.AllocateBestPeer(new List<PeerInfo>(), Substitute.For<INodeStatsManager>(), ctx.BlockTree);
        ctx.PeerPool
            .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>())
            .Returns(Task.FromResult(peerAllocation));

        // Need to be asleep at this time
        ctx.Feed.FallAsleep();

        CancellationTokenSource cts = new CancellationTokenSource();

        Task ignored = ctx.Dispatcher.Start(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Feed should activate and allocate the first peer
        Task accidentalDeadlockTask = Task.Factory.StartNew(() => ctx.Feed.Activate(), TaskCreationOptions.LongRunning);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // At this point, chain level helper is block, we then trigger replaced.
        ISyncPeer syncPeer2 = Substitute.For<ISyncPeer>();
        syncPeer2.Node.Returns(new Node(TestItem.PublicKeyB, "127.0.0.2", 9999));
        syncPeer2.HeadNumber.Returns(4);

        // It will now get replaced with syncPeer2
        peerAllocationStrategy.ClearSubstitute();
        peerAllocationStrategy
            .Allocate(Arg.Any<PeerInfo?>(), Arg.Any<IEnumerable<PeerInfo>>(), Arg.Any<INodeStatsManager>(), Arg.Any<IBlockTree>())
            .Returns(new PeerInfo(syncPeer2));
        peerAllocation.AllocateBestPeer(new List<PeerInfo>(), Substitute.For<INodeStatsManager>(), ctx.BlockTree);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Release it
        chainLevelHelperBlocker.Set();

        // Just making sure...
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.That(() => accidentalDeadlockTask.IsCompleted, Is.True.After(1000, 100));
        cts.Cancel();
        cts.Dispose();
    }

    [Test]
    public async Task No_old_bodies_and_receipts()
    {
        BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder blockTrees = BlockTreeTests.BlockTreeTestScenario
            .GoesLikeThis()
            .WithBlockTrees(4, 129)
            .InsertBeaconPivot(64)
            .InsertBeaconHeaders(4, 128);
        BlockTree syncedTree = blockTrees.SyncedTree;
        PostMergeContext ctx = new();
        ctx.BlockTreeScenario = blockTrees;

        ctx.Feed = new FastSyncFeed(ctx.SyncModeSelector,
            new SyncConfig
            {
                NonValidatorNode = true,
                DownloadBodiesInFastSync = false,
                DownloadReceiptsInFastSync = false
            }, LimboLogs.Instance);

        ctx.BeaconPivot.EnsurePivot(blockTrees.SyncedTree.FindHeader(64, BlockTreeLookupOptions.None));

        SyncPeerMock syncPeer = new(syncedTree, false, Response.AllCorrect, 34000000);
        PeerInfo peerInfo = new(syncPeer);

        IPeerAllocationStrategy peerAllocationStrategy = Substitute.For<IPeerAllocationStrategy>();

        peerAllocationStrategy
            .Allocate(Arg.Any<PeerInfo?>(), Arg.Any<IEnumerable<PeerInfo>>(), Arg.Any<INodeStatsManager>(), Arg.Any<IBlockTree>())
            .Returns(peerInfo);
        SyncPeerAllocation peerAllocation = new(peerAllocationStrategy, AllocationContexts.Blocks);
        peerAllocation.AllocateBestPeer(new List<PeerInfo>(), Substitute.For<INodeStatsManager>(), ctx.BlockTree);

        ctx.PeerPool
            .Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>())
            .Returns(Task.FromResult(peerAllocation));

        ctx.Feed.Activate();

        CancellationTokenSource cts = new();
        ctx.Dispatcher.Start(cts.Token);

        Assert.That(
            () => ctx.BlockTree.BestKnownNumber,
            Is.EqualTo(96).After(3000, 100)
        );

        cts.Cancel();
    }

    [TestCase(DownloaderOptions.WithReceipts)]
    [TestCase(DownloaderOptions.None)]
    [TestCase(DownloaderOptions.Process)]
    public async Task BlockDownloader_works_correctly_with_withdrawals(int options)
    {
        PostMergeContext ctx = new();
        DownloaderOptions downloaderOptions = (DownloaderOptions)options;
        bool withReceipts = downloaderOptions == DownloaderOptions.WithReceipts;
        BlockDownloader downloader = ctx.BlockDownloader;

        Response responseOptions = Response.AllCorrect;
        if (withReceipts)
        {
            responseOptions |= Response.WithTransactions;
        }

        int headNumber = 5;

        // normally chain length should be head number + 1 so here we setup a slightly shorter chain which
        // will only be fixed slightly later
        long chainLength = headNumber + 1;
        SyncPeerMock syncPeerInternal = new(chainLength, withReceipts, responseOptions, true);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetBlockHeaders(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockHeaders(ci.ArgAt<long>(0), ci.ArgAt<int>(1), ci.ArgAt<int>(2), ci.ArgAt<CancellationToken>(3)));

        syncPeer.GetBlockBodies(Arg.Any<IReadOnlyList<Keccak>>(), Arg.Any<CancellationToken>())
            .Returns(ci => syncPeerInternal.GetBlockBodies(ci.ArgAt<IReadOnlyList<Keccak>>(0), ci.ArgAt<CancellationToken>(1)));

        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Keccak>>(), Arg.Any<CancellationToken>())
            .Returns(async ci => await syncPeerInternal.GetReceipts(ci.ArgAt<IReadOnlyList<Keccak>>(0), ci.ArgAt<CancellationToken>(1)));


        syncPeer.TotalDifficulty.Returns(ci => syncPeerInternal.TotalDifficulty);
        syncPeer.HeadHash.Returns(ci => syncPeerInternal.HeadHash);
        syncPeer.HeadNumber.Returns(ci => syncPeerInternal.HeadNumber);

        PeerInfo peerInfo = new(syncPeer);

        int threshold = 2;
        await downloader.DownloadHeaders(peerInfo, new BlocksRequest(DownloaderOptions.None, threshold), CancellationToken.None);
        ctx.BlockTree.BestSuggestedHeader.Number.Should().Be(Math.Max(0, Math.Min(headNumber, headNumber - threshold)));

        syncPeerInternal.ExtendTree(chainLength * 2);
        Func<Task> action = async () => await downloader.DownloadBlocks(peerInfo, new BlocksRequest(downloaderOptions), CancellationToken.None);

        await action.Should().NotThrowAsync();
    }

    class PostMergeContext : Context
    {
        protected override ISpecProvider SpecProvider => _specProvider ??= new MainnetSpecProvider(); // PoSSwitcher changes TTD, so can't use MainnetSpecProvider.Instance

        private BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder? _blockTreeScenario = null;

        public BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder BlockTreeScenario
        {
            get =>
                _blockTreeScenario ??
                new BlockTreeTests.BlockTreeTestScenario.ScenarioBuilder();
            set => _blockTreeScenario = value;
        }

        public override IBlockTree BlockTree => _blockTreeScenario?.NotSyncedTree ?? base.BlockTree;

        private MemDb? _metadataDb = null;
        private MemDb MetadataDb => (_metadataDb ?? _blockTreeScenario?.NotSyncedTreeBuilder?.MetadataDb) ?? (_metadataDb ??= new MemDb());

        private MergeConfig _mergeConfig;

        public MergeConfig MergeConfig
        {
            get => _mergeConfig ??= new MergeConfig() { TerminalTotalDifficulty = "58750000000000000000000" }; // Main block downloader test assume pre-merge
            set => _mergeConfig = value;
        }

        private BeaconPivot? _beaconPivot = null;
        public BeaconPivot BeaconPivot => _beaconPivot ??= new(new SyncConfig(), MetadataDb, BlockTree, LimboLogs.Instance);

        private PoSSwitcher? _posSwitcher = null;

        public PoSSwitcher PosSwitcher => _posSwitcher ??= new(
            MergeConfig,
            new SyncConfig(),
            MetadataDb,
            BlockTree,
            SpecProvider,
            LimboLogs.Instance);

        protected override IBetterPeerStrategy BetterPeerStrategy => _betterPeerStrategy ??=
            new MergeBetterPeerStrategy(new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), PosSwitcher, BeaconPivot, LimboLogs.Instance);

        private IChainLevelHelper? _chainLevelHelper = null;

        public IChainLevelHelper ChainLevelHelper
        {
            get =>
                _chainLevelHelper ??= new ChainLevelHelper(
                    BlockTree,
                    BeaconPivot,
                    new SyncConfig(),
                    LimboLogs.Instance);
            set => _chainLevelHelper = value;
        }

        private MergeBlockDownloader? _mergeBlockDownloader;

        public override BlockDownloader BlockDownloader
        {
            get
            {
                return _mergeBlockDownloader ??= new(
                    PosSwitcher,
                    BeaconPivot,
                    Feed,
                    PeerPool,
                    BlockTree,
                    BlockValidator,
                    SealValidator,
                    NullSyncReport.Instance,
                    ReceiptStorage,
                    SpecProvider,
                    BetterPeerStrategy,
                    ChainLevelHelper,
                    Substitute.For<ISyncProgressResolver>(),
                    LimboLogs.Instance);
            }
        }

        private IPeerAllocationStrategyFactory<BlocksRequest>? _peerAllocationStrategy;
        protected override IPeerAllocationStrategyFactory<BlocksRequest> PeerAllocationStrategy =>
            _peerAllocationStrategy ??= new MergeBlocksSyncPeerAllocationStrategyFactory(PosSwitcher, BeaconPivot, LimboLogs.Instance);

    }
}

// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
using Nethermind.Core.Specs;
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
        MergeContext ctx = new();
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
        MergeContext ctx = new();
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

        MergeContext ctx = new();
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
        MergeContext ctx = new();
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

        MergeContext ctx = new();
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

    class MergeContext : Context
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

        private IChainLevelHelper? _chainLevelHelper = null;
        public IChainLevelHelper ChainLevelHelper => _chainLevelHelper ??= new ChainLevelHelper(
            BlockTree,
            BeaconPivot,
            new SyncConfig(),
            LimboLogs.Instance);

        private MergeBlockDownloader? _mergeBlockDownloader;
        public override BlockDownloader BlockDownloader
        {
            get
            {
                TotalDifficultyBetterPeerStrategy preMergePeerStrategy = new(LimboLogs.Instance);
                MergeBetterPeerStrategy betterPeerStrategy = new(preMergePeerStrategy, PosSwitcher, BeaconPivot, LimboLogs.Instance);

                return _mergeBlockDownloader ?? new(
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
                    betterPeerStrategy,
                    ChainLevelHelper,
                    Substitute.For<ISyncProgressResolver>(),
                    LimboLogs.Instance);
            }
        }
    }
}

// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.History;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Reporting;
using NSubstitute;
using NUnit.Framework;
using IContainer = Autofac.IContainer;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [TestCase(false, false, false, false)]
    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    // Having served the block's body, the peer has the block, so withholding its list is a breach.
    [TestCase(false, false, true, false)]
    // A peer ahead of our local head may have pruned the list while retaining the body.
    [TestCase(false, false, true, true)]
    [TestCase(true, false, true, true)]
    // An advertised head past our best known header does not move the block out of the serving window.
    [TestCase(false, false, true, true, false)]
    // Processing can go ahead without the list, so the peer may still be backfilling it.
    [TestCase(false, false, true, false, true, true)]
    // Missed slots leave the block inside the height window yet older than the retention period.
    [TestCase(false, false, true, false, true, false, true)]
    public async Task Reports_peer_returning_unavailable_block_access_list(bool isStatic, bool isTrusted, bool peerServedBody, bool peerPastRetention, bool peerHeadKnown = true, bool processing = false, bool pastRetentionPeriod = false)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        IBlockTree farKnownBlockTree = Substitute.For<IBlockTree>();
        farKnownBlockTree.BestKnownNumber.Returns(ulong.MaxValue);
        ulong retentionSeconds = HistoryRetentionConstants.HistoryPruneEpochs * 32 * new BlocksConfig().SecondsPerSlot;
        ulong now = HeaderTimestamp + (pastRetentionPeriod ? retentionSeconds + 1 : retentionSeconds);
        await using IContainer node = CreateNode(builder =>
        {
            builder
                .AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider)
                .AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger)))
                .AddSingleton<ITimestamper>(new ManualTimestamper(DateTime.UnixEpoch.AddSeconds(now)));
            if (peerPastRetention && peerHeadKnown)
            {
                builder.AddSingleton<BlockAccessListServingWindow, IHistoryPruner, ISpecProvider, ITimestamper, IBlocksConfig>(
                    (historyPruner, specProvider, timestamper, blocksConfig) => new(farKnownBlockTree, historyPruner, specProvider, timestamper, blocksConfig));
            }

            if (processing) builder.AddSingleton<BlockDownloader, AccessListOnProcessingBlockDownloader>();
        });
        Context ctx = node.Resolve<Context>();
        DownloaderOptions options = processing ? DownloaderOptions.Process : DownloaderOptions.Insert;
        byte[] servedAccessList = [0xc0];
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider, TestItem.KeccakA, Keccak.Compute(servedAccessList));
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
        syncPeer.HeadNumber.Returns(peerPastRetention ? ulong.MaxValue : 2UL);
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303, isStatic) { IsTrusted = isTrusted });
        PeerInfo peerInfo = new(syncPeer);

        using BlocksRequest bodyRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(options, 0, CancellationToken.None))!;
        if (peerServedBody)
        {
            bodyRequest.OwnedBodies = new OwnedBlockBodies([new BlockBody(), new BlockBody()]);
            ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(bodyRequest, peerInfo);
        }

        // Serving one list does not offset withholding another, and a repeated answer does not repeat the warning.
        byte[]?[][] responses = [[null, servedAccessList], [null]];
        List<SyncResponseHandlingResult> results = [];
        foreach (byte[]?[] response in responses)
        {
            using BlocksRequest request = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(options, 0, CancellationToken.None))!;
            request.BlockAccessLists = BuildBlockAccessLists(response);
            results.Add(ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(request, peerInfo));
        }

        bool breach = peerServedBody && !(peerPastRetention && peerHeadKnown) && !processing && !pastRetentionPeriod;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(results, Is.All.EqualTo(SyncResponseHandlingResult.LesserQuality));
            ctx.PeerPool.Received(breach ? responses.Length : 0).ReportBreachOfProtocol(
                Arg.Any<PeerInfo>(),
                Arg.Any<DisconnectReason>(),
                Arg.Any<string>());
            logger.Received(isStatic || isTrusted ? 1 : 0).Warn(Arg.Is<string>(message => message.Contains("block access list")));
        }
    }

    [Test]
    public async Task Prepares_only_one_download_type_per_request()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);

        using BlocksRequest bodyRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Insert,
            0,
            CancellationToken.None))!;

        Assert.That(bodyRequest.BodiesRequests.Count, Is.EqualTo(1));
        Assert.That(bodyRequest.BlockAccessListsRequests.Count, Is.EqualTo(0));
        Assert.That(bodyRequest.ReceiptsRequests.Count, Is.EqualTo(0));

        using BlocksRequest blockAccessListRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Insert,
            0,
            CancellationToken.None))!;

        Assert.That(blockAccessListRequest.BodiesRequests.Count, Is.EqualTo(0));
        Assert.That(blockAccessListRequest.BlockAccessListsRequests.Count, Is.EqualTo(1));
        Assert.That(blockAccessListRequest.ReceiptsRequests.Count, Is.EqualTo(0));

        BlocksRequest? noRequest = await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Insert,
            0,
            CancellationToken.None);

        Assert.That(noRequest, Is.Null);
    }

    [Test]
    public async Task Sizes_each_request_type_by_the_peers_that_serve_it([Values] bool blockAccessListsPeerFree)
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(blockAccessListsPeerFree ? EthVersions.Eth71 : EthVersions.Eth70);
        syncPeer.IsInitialized.Returns(true);
        ctx.PeerPool.InitializedPeers.Returns([new PeerInfo(syncPeer)]);

        using BlocksRequest bodyRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(DownloaderOptions.Insert, 0, CancellationToken.None))!;
        using BlocksRequest blockAccessListRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(DownloaderOptions.Insert, 0, CancellationToken.None))!;

        await ctx.PeerPool.Received().EstimateRequestLimit(RequestType.Bodies, BlocksSyncPeerAllocationStrategyFactory.AllocationStrategy, AllocationContexts.Blocks, Arg.Any<CancellationToken>());
        await ctx.PeerPool.Received().EstimateRequestLimit(RequestType.Receipts, BlocksSyncPeerAllocationStrategyFactory.ReceiptsAllocationStrategy, AllocationContexts.Blocks, Arg.Any<CancellationToken>());
        // Without a free eth/71 peer the estimate would only wait for an allocation, so the default size is used.
        await ctx.PeerPool.Received(blockAccessListsPeerFree ? 1 : 0).EstimateRequestLimit(RequestType.BlockAccessLists, BlocksSyncPeerAllocationStrategyFactory.BlockAccessListsAllocationStrategy, AllocationContexts.BlockAccessLists, Arg.Any<CancellationToken>());
        Assert.That(blockAccessListRequest.BlockAccessListsRequests.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task Full_sync_processing_does_not_request_block_access_lists()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);

        using BlocksRequest bodyRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Process,
            0,
            CancellationToken.None))!;

        Assert.That(bodyRequest.BodiesRequests.Count, Is.EqualTo(1));
        Assert.That(bodyRequest.BlockAccessListsRequests.Count, Is.EqualTo(0));

        BlocksRequest? noRequest = await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Process,
            0,
            CancellationToken.None);

        Assert.That(noRequest, Is.Null);
    }

    [Test]
    public async Task Full_sync_processing_satisfies_block_without_downloaded_block_access_list()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);

        BlocksRequest bodyRequest = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Process,
            0,
            CancellationToken.None))!;
        bodyRequest.OwnedBodies = new OwnedBlockBodies([new BlockBody()]);

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        PeerInfo peerInfo = new(syncPeer);

        SyncResponseHandlingResult result = ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(bodyRequest, peerInfo);
        BlocksRequest? noRequest = await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Process,
            0,
            CancellationToken.None);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
        Assert.That(noRequest, Is.Null);
        Assert.That(ctx.BlockTree.BestSuggestedHeader!.Number, Is.EqualTo(1));
    }

    [Test]
    public async Task Does_not_request_block_access_lists_from_pre_eth71_peer()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);

        BlocksRequest request = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Insert,
            0,
            CancellationToken.None))!;
        request.BodiesRequests.Dispose();
        request.BodiesRequests = new ArrayPoolList<BlockHeader>(0);
        request.ReceiptsRequests.Dispose();
        request.ReceiptsRequests = new ArrayPoolList<BlockHeader>(0);

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth70);
        PeerInfo peerInfo = new(syncPeer);

        await ctx.FullSyncFeedComponent.Downloader.Dispatch(peerInfo, request, CancellationToken.None);
        SyncResponseHandlingResult result = ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(request, peerInfo);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
        await syncPeer.DidNotReceive().GetBlockAccessLists(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>());
        ctx.PeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
    }

    private const ulong HeaderTimestamp = 1_000_000;

    private static void ConfigureBlockAccessListRequest(Context ctx, IForwardHeaderProvider forwardHeaderProvider, params Hash256[] accessListHashes)
    {
        if (accessListHashes.Length == 0) accessListHashes = [TestItem.KeccakA];

        BlockHeader[] chain = new BlockHeader[accessListHashes.Length + 1];
        chain[0] = ctx.BlockTree.Genesis!;
        for (int i = 0; i < accessListHashes.Length; i++)
        {
            chain[i + 1] = Build.A.BlockHeader
                .WithParent(chain[i])
                .WithTimestamp(HeaderTimestamp)
                .WithBlockAccessListHash(accessListHashes[i])
                .TestObject;
        }

        forwardHeaderProvider
            .GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                IOwnedReadOnlyList<BlockHeader?> headers = new ArrayPoolList<BlockHeader?>(chain.Length, chain);
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(headers);
            });

        ctx.PeerPool
            .EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(accessListHashes.Length));
    }

    private static IOwnedReadOnlyList<byte[]?> BuildBlockAccessLists(params byte[]?[] blockAccessLists) =>
        new ArrayPoolList<byte[]?>(blockAccessLists);

    /// <summary>Requests block access lists while processing, as the merge downloader does for reconstructable blocks.</summary>
    private sealed class AccessListOnProcessingBlockDownloader(
        IBlockTree blockTree,
        IBlockValidator blockValidator,
        ISyncReport syncReport,
        IReceiptStorage receiptStorage,
        ISpecProvider specProvider,
        IBetterPeerStrategy betterPeerStrategy,
        IFullStateFinder fullStateFinder,
        IForwardHeaderProvider forwardHeaderProvider,
        ISyncPeerPool syncPeerPool,
        IReceiptsRecovery receiptsRecovery,
        IBlockProcessingQueue processingQueue,
        ISyncConfig syncConfig,
        BlockAccessListServingWindow balServingWindow,
        ILogManager logManager)
        : BlockDownloader(blockTree, blockValidator, syncReport, receiptStorage, specProvider, betterPeerStrategy, fullStateFinder,
            forwardHeaderProvider, syncPeerPool, receiptsRecovery, processingQueue, syncConfig, balServingWindow, logManager)
    {
        protected override bool ShouldDownloadAccessList(BlockHeader header, bool shouldProcess) => true;
    }
}

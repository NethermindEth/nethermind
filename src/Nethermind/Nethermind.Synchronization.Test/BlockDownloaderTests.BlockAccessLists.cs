// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Contract.P2P;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using IContainer = Autofac.IContainer;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    [Test]
    public async Task Does_not_report_breach_when_peer_returns_unavailable_block_access_list()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ConfigureBlockAccessListRequest(ctx, forwardHeaderProvider);

        BlocksRequest request = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(
            DownloaderOptions.Insert,
            0,
            CancellationToken.None))!;
        request.BlockAccessLists = BuildBlockAccessLists((byte[]?)null);

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.ProtocolVersion.Returns(EthVersions.Eth71);
        PeerInfo peerInfo = new(syncPeer);

        SyncResponseHandlingResult result = ctx.FullSyncFeedComponent.BlockDownloader.HandleResponse(request, peerInfo);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
        ctx.PeerPool.DidNotReceive().ReportBreachOfProtocol(
            Arg.Any<PeerInfo>(),
            Arg.Any<DisconnectReason>(),
            Arg.Any<string>());
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

    private static void ConfigureBlockAccessListRequest(Context ctx, IForwardHeaderProvider forwardHeaderProvider)
    {
        BlockHeader parent = ctx.BlockTree.Genesis!;
        BlockHeader header = Build.A.BlockHeader
            .WithParent(parent)
            .WithBlockAccessListHash(TestItem.KeccakA)
            .TestObject;

        forwardHeaderProvider
            .GetBlockHeaders(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                IOwnedReadOnlyList<BlockHeader?> headers = new ArrayPoolList<BlockHeader?>(2) { parent, header };
                return Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(headers);
            });

        ctx.PeerPool
            .EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(1));
    }

    private static IOwnedReadOnlyList<byte[]?> BuildBlockAccessLists(params byte[]?[] blockAccessLists) =>
        new ArrayPoolList<byte[]?>(blockAccessLists);

}

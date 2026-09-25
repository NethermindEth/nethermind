// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using IContainer = Autofac.IContainer;

namespace Nethermind.Synchronization.Test;

public partial class BlockDownloaderTests
{
    private const int ServingPeerLimit = 64;
    private const int ChainLength = 256;

    /// <summary>
    /// Full sync below the history floor of most peers (gnosis archive sync: the default config prunes bodies below
    /// the merge). The pruned peers answer faster, so the throwaway estimate used to land on one of them, whose limit
    /// had collapsed to 1 on empty answers, and every request sent to the peer that does serve carried one body.
    /// </summary>
    [TestCase(0UL, 2UL, TestName = "Unannounced serving peer, pruned peers announce the next block")]
    [TestCase(0UL, 1_000UL, TestName = "Unannounced serving peer, pruned peers announce a far floor")]
    [TestCase(1UL, 1_000UL, TestName = "Serving peer announces the window's first block")]
    public async Task Bodies_request_is_sized_by_a_peer_that_stores_the_window(ulong servingEarliest, ulong prunedEarliest)
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ServeChain(ctx, forwardHeaderProvider, ChainLength);

        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo serving = StatsPeer(stats, TestItem.PublicKeyA, servingEarliest, speed: 10, limit: ServingPeerLimit);
        PeerInfo[] peers =
        [
            StatsPeer(stats, TestItem.PublicKeyB, prunedEarliest, speed: 1_000, limit: 1),
            StatsPeer(stats, TestItem.PublicKeyC, prunedEarliest, speed: 1_000, limit: 1),
            StatsPeer(stats, TestItem.PublicKeyD, prunedEarliest, speed: 1_000, limit: 1),
            serving,
        ];
        EstimateLikeThePool(ctx, peers, stats);

        using BlocksRequest request = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.BodiesRequests.Count, Is.EqualTo(ServingPeerLimit));
            Assert.That(new BlocksSyncPeerAllocationStrategyFactory().Create(request).Allocate(null, peers, stats, ctx.BlockTree), Is.SameAs(serving),
                "the request must be dispatched to a peer that stores it");
        }
    }

    [Test]
    public async Task Receipts_are_not_estimated_when_not_downloaded()
    {
        IForwardHeaderProvider forwardHeaderProvider = Substitute.For<IForwardHeaderProvider>();
        await using IContainer node = CreateNode(builder => builder.AddSingleton<IForwardHeaderProvider>(forwardHeaderProvider));
        Context ctx = node.Resolve<Context>();
        ServeChain(ctx, forwardHeaderProvider, ChainLength);
        ctx.PeerPool
            .EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<int?>(ServingPeerLimit));

        using BlocksRequest request = (await ctx.FullSyncFeedComponent.BlockDownloader.PrepareRequest(DownloaderOptions.Process, 0, CancellationToken.None))!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.BodiesRequests.Count, Is.EqualTo(ServingPeerLimit));
            _ = ctx.PeerPool.Received(1).EstimateRequestLimit(RequestType.Bodies, Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>());
            _ = ctx.PeerPool.DidNotReceive().EstimateRequestLimit(RequestType.Receipts, Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>());
        }
    }

    private static void ServeChain(Context ctx, IForwardHeaderProvider forwardHeaderProvider, int length)
    {
        BlockHeader[] chain = new BlockHeader[length + 1];
        chain[0] = ctx.BlockTree.Genesis!;
        for (int i = 1; i <= length; i++)
        {
            chain[i] = Build.A.BlockHeader.WithParent(chain[i - 1]).TestObject;
        }

        forwardHeaderProvider
            .GetBlockHeaders(Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<BlockHeader?>?>(new ArrayPoolList<BlockHeader?>(chain.Length, chain)));
    }

    // SyncPeerPool.EstimateRequestLimit: the limit of the peer the strategy picks next.
    private static void EstimateLikeThePool(Context ctx, IReadOnlyList<PeerInfo> peers, INodeStatsManager stats) =>
        ctx.PeerPool
            .EstimateRequestLimit(Arg.Any<RequestType>(), Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                PeerInfo? next = ci.ArgAt<IPeerAllocationStrategy>(1).Allocate(null, peers, stats, ctx.BlockTree);
                return Task.FromResult(next is null ? null : (int?)stats.GetOrAdd(next.SyncPeer.Node).GetCurrentRequestLimit(ci.ArgAt<RequestType>(0)));
            });

    private static PeerInfo StatsPeer(INodeStatsManager stats, PublicKey key, ulong earliest, long speed, int limit)
    {
        Node peerNode = new(key, "127.0.0.1", 30303);
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(peerNode);
        syncPeer.IsInitialized.Returns(true);
        syncPeer.EarliestBlock.Returns(earliest);

        INodeStats nodeStats = Substitute.For<INodeStats>();
        nodeStats.GetAverageTransferSpeed(TransferSpeedType.Bodies).Returns(speed);
        nodeStats.GetCurrentRequestLimit(RequestType.Bodies).Returns(limit);
        stats.GetOrAdd(peerNode).Returns(nodeStats);
        return new PeerInfo(syncPeer);
    }
}

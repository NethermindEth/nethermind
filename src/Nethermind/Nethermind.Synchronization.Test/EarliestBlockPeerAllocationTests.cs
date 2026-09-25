// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
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

namespace Nethermind.Synchronization.Test;

[Parallelizable(ParallelScope.All)]
public class EarliestBlockPeerAllocationTests
{
    private const ulong GnosisBarrier = 25_349_537;

    [Test]
    public void Skips_peers_that_announced_they_no_longer_store_the_block()
    {
        PeerInfo archive = Peer(TestItem.PublicKeyA, earliest: 1);
        PeerInfo unannounced = Peer(TestItem.PublicKeyB, earliest: 0);
        PeerInfo pruned = Peer(TestItem.PublicKeyC, earliest: GnosisBarrier);
        RecordingStrategy inner = new();

        new EarliestBlockPeerAllocationStrategy(inner, 10_830_000).Allocate(pruned, [archive, unannounced, pruned], Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.Offered, Is.EquivalentTo(new[] { archive, unannounced }));
            Assert.That(inner.Current, Is.Null, "a current peer that cannot serve the block must not be kept");
        }
    }

    [TestCase(GnosisBarrier)]
    [TestCase(GnosisBarrier + 1)]
    [TestCase(48_000_000UL)]
    public void Keeps_every_peer_for_blocks_they_all_store(ulong blockNumber)
    {
        PeerInfo[] peers = [Peer(TestItem.PublicKeyA, earliest: 1), Peer(TestItem.PublicKeyB, earliest: 0), Peer(TestItem.PublicKeyC, earliest: GnosisBarrier)];
        RecordingStrategy inner = new();

        new EarliestBlockPeerAllocationStrategy(inner, blockNumber).Allocate(peers[2], peers, Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inner.Offered, Is.EquivalentTo(peers));
            Assert.That(inner.Current, Is.SameAs(peers[2]));
        }
    }

    [TestCase(10UL, 20UL, 30UL, 10UL)]
    [TestCase(0UL, 20UL, 30UL, 20UL)]
    [TestCase(0UL, 0UL, 30UL, 30UL)]
    public void Blocks_request_strategy_filters_on_the_lowest_requested_block(ulong firstBody, ulong firstAccessList, ulong firstReceipt, ulong expectedFloor)
    {
        using BlocksRequest request = new()
        {
            BodiesRequests = Headers(firstBody),
            BlockAccessListsRequests = Headers(firstAccessList),
            ReceiptsRequests = Headers(firstReceipt),
        };

        PeerInfo atFloor = Peer(TestItem.PublicKeyA, earliest: expectedFloor, supportsAccessLists: true);
        PeerInfo aboveFloor = Peer(TestItem.PublicKeyB, earliest: expectedFloor + 1, supportsAccessLists: true);
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();

        IPeerAllocationStrategy strategy = new BlocksSyncPeerAllocationStrategyFactory().Create(request);

        Assert.That(strategy.Allocate(null, [aboveFloor, atFloor], stats, Substitute.For<IBlockTree>()), Is.SameAs(atFloor));
    }

    [Test]
    public void Blocks_request_strategy_without_requests_keeps_every_peer()
    {
        using BlocksRequest request = new();
        PeerInfo pruned = Peer(TestItem.PublicKeyA, earliest: GnosisBarrier);

        IPeerAllocationStrategy strategy = new BlocksSyncPeerAllocationStrategyFactory().Create(request);

        Assert.That(strategy.Allocate(null, [pruned], Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>()), Is.SameAs(pruned));
    }

    [Test]
    public void Nobody_is_allocated_when_every_peer_pruned_the_block()
    {
        PeerInfo[] peers = [Peer(TestItem.PublicKeyA, earliest: GnosisBarrier), Peer(TestItem.PublicKeyB, earliest: GnosisBarrier)];

        PeerInfo? allocated = BlocksSyncPeerAllocationStrategyFactory.ForBlocksFrom(10_830_000)
            .Allocate(null, peers, Substitute.For<INodeStatsManager>(), Substitute.For<IBlockTree>());

        Assert.That(allocated, Is.Null);
    }

    private static IOwnedReadOnlyList<BlockHeader> Headers(ulong first) =>
        first == 0
            ? IOwnedReadOnlyList<BlockHeader>.Empty
            : new ArrayPoolList<BlockHeader>(2) { Build.A.BlockHeader.WithNumber(first).TestObject, Build.A.BlockHeader.WithNumber(first + 1).TestObject };

    private static PeerInfo Peer(PublicKey key, ulong earliest, bool supportsAccessLists = false)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(key, "127.0.0.1", 30303));
        syncPeer.EarliestBlock.Returns(earliest);
        syncPeer.IsInitialized.Returns(true);
        syncPeer.ProtocolVersion.Returns(supportsAccessLists ? (byte)71 : (byte)69);
        return new PeerInfo(syncPeer);
    }

    private sealed class RecordingStrategy : IPeerAllocationStrategy
    {
        public List<PeerInfo> Offered { get; } = [];
        public PeerInfo? Current { get; private set; }

        public PeerInfo? Allocate(PeerInfo? currentPeer, IEnumerable<PeerInfo> peers, INodeStatsManager nodeStatsManager, IBlockTree blockTree)
        {
            Current = currentPeer;
            Offered.AddRange(peers);
            return Offered.FirstOrDefault();
        }
    }
}

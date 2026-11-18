// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using PublicKey = Nethermind.Core.Crypto.PublicKey;

namespace Nethermind.Synchronization.Test.AllocationStrategies;

public class BySpeedStrategyTests
{
    [Test]
    public void Slow_headers_prefer_proven_peers_and_bound_unproven_probes(
        [Values(null, 0)] long? unprovenSpeed, [Values] bool newBatchStrategy)
    {
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo unproven = CreatePeerInfoWithSpeed(unprovenSpeed, stats);
        PeerInfo proven = CreatePeerInfoWithSpeed(10, stats);
        BySpeedStrategy strategy = new(TransferSpeedType.Headers, false);
        FastBlocksPeerAllocationStrategyFactory factory = new();
        int probes = 0;
        for (int i = 0; i < 64; i++)
        {
            using HeadersSyncBatch batch = new() { StartNumber = 0, RequestSize = 1 };
            IPeerAllocationStrategy selectedStrategy = newBatchStrategy ? factory.Create(batch) : strategy;
            if (selectedStrategy.Allocate(null, [unproven, proven], stats, Build.A.BlockTree().TestObject) == unproven) probes++;
        }
        Assert.That(probes, Is.InRange(1, 2));
    }

    [Test]
    public void Slow_headers_do_not_prefer_recent_failures_and_allow_recovery()
    {
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo failed = CreatePeerInfoWithSpeed(1, stats);
        PeerInfo healthy = CreatePeerInfoWithSpeed(10, stats);
        failed.RecordNoProgress(AllocationContexts.Headers);
        BySpeedStrategy strategy = new(TransferSpeedType.Headers, false, recalculateSpeedProbability: 0);
        Assert.That(strategy.Allocate(failed, [failed, healthy], stats, Build.A.BlockTree().TestObject), Is.SameAs(healthy));
        failed.RecordProgress(AllocationContexts.Headers);
        Assert.That(strategy.Allocate(null, [failed, healthy], stats, Build.A.BlockTree().TestObject), Is.SameAs(failed));
    }

    private static PublicKey TestPublicKey { get; } = new(Bytes.FromHexString(
        "0x13a1107b6f78a4977222d2d5a4cd05a8a042b75222c8ec99129b83793eda3d214208d4e835617512fc8d148d3d1b4d89530861644f531675b1fb64b785c6c152"));

    [TestCase(1, 0, 0, 2)]
    [TestCase(2, 0, 0, 2)]
    [TestCase(3, 0, 0, 2)]
    [TestCase(null, 0, 0, 2)]
    [TestCase(1, 0.5, 0, 1)]
    [TestCase(1, 0.0, 50, 1)]
    [TestCase(1, 0.0, 10, 2)]
    [TestCase(1, 0.1, 0, 2)]
    public void TestShouldSelectHighestSpeed(int? currentPeerIdx, decimal minDiffPercentageForSpeedSwitch, int minDiffSpeed, int expectedSelectedPeerIdx)
    {
        long[] peerSpeeds =
        {
            100,
            90,
            120,
            50
        };

        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();

        List<PeerInfo> peers = peerSpeeds
            .Select(peerSpeed => CreatePeerInfoWithSpeed(peerSpeed, nodeStatsManager))
            .ToList();

        BySpeedStrategy strategy = new(TransferSpeedType.Bodies, true, minDiffPercentageForSpeedSwitch, minDiffSpeed, 0, 0);

        PeerInfo? currentPeer = null;
        if (currentPeerIdx is not null) currentPeer = peers[currentPeerIdx.Value];

        PeerInfo selectedPeer = strategy.Allocate(currentPeer, peers, nodeStatsManager, Build.A.BlockTree().TestObject)!;

        int selectedPeerIdx = peers.IndexOf(selectedPeer);
        Assert.That(selectedPeerIdx, Is.EqualTo(expectedSelectedPeerIdx));
    }

    [TestCase(1, 0, 0, false)]
    [TestCase(0, 1, 0, true)]
    [TestCase(1, 1, 1, false)]
    [TestCase(1, 1, 2, true)]
    [TestCase(10, 10, 2, false)]
    [TestCase(10, 10, 11, true)]
    [TestCase(10, 0, 11, false)]
    public void TestMinimumKnownSpeed(int peerWithKnownSpeed, int peerWithUnknownSpeed, int desiredKnownPeer, bool pickedNewPeer)
    {
        long?[] peerSpeeds = Enumerable.Repeat<long?>(100, peerWithKnownSpeed)
            .Concat(Enumerable.Repeat<long?>(null, peerWithUnknownSpeed))
            .ToArray();

        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();

        List<PeerInfo> peers = peerSpeeds
            .Select(peerSpeed => CreatePeerInfoWithSpeed(peerSpeed, nodeStatsManager))
            .ToList();

        BySpeedStrategy strategy = new(TransferSpeedType.Bodies, true, 0, 0, 0, desiredKnownPeer);

        PeerInfo selectedPeer = strategy.Allocate(null, peers, nodeStatsManager, Build.A.BlockTree().TestObject)!;

        int selectedPeerIdx = peers.IndexOf(selectedPeer);
        if (pickedNewPeer)
        {
            Assert.That(selectedPeerIdx, Is.EqualTo(peerWithKnownSpeed)); // It picked the first peer with unknown speed
        }
        else
        {
            Assert.That(selectedPeerIdx, Is.LessThan(peerWithKnownSpeed)); // It picked earlier peers which have known speed
        }
    }

    [Test]
    public void TestWhenSameSpeed_RandomlyTryOtherPeer()
    {
        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();

        List<PeerInfo> peers = Enumerable.Repeat<long?>(10, 50)
            .Concat(Enumerable.Repeat<long?>(100, 50))
            .Select((speed) => CreatePeerInfoWithSpeed(speed, nodeStatsManager))
            .ToList();

        BySpeedStrategy strategy = new(TransferSpeedType.Bodies, true, 0, 0, 0, 0);

        PeerInfo selectedPeer = strategy.Allocate(null, peers, nodeStatsManager, Build.A.BlockTree().TestObject)!;
        int selectedPeerIdx = peers.IndexOf(selectedPeer);
        Assert.That(selectedPeerIdx, Is.GreaterThan(50));
    }

    [TestCase(10, 0, 0, 0)]
    [TestCase(10, 0, 1, 0)]
    [TestCase(10, 10, 1, 0.5)]
    [TestCase(10, 10, 0.5, 0.25)]
    public void TestRecalculateSpeedProbability(int peerWithKnownSpeed, int peerWithUnknownSpeed, double recalculateSpeedProbability, double chanceOfPickingPeerWithNoSpeed)
    {
        long?[] peerSpeeds = Enumerable.Repeat<long?>(100, peerWithKnownSpeed)
            .Concat(Enumerable.Repeat<long?>(null, peerWithUnknownSpeed))
            .ToArray();

        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();

        List<PeerInfo> peers = peerSpeeds
            .Select(peerSpeed => CreatePeerInfoWithSpeed(peerSpeed, nodeStatsManager))
            .ToList();

        BySpeedStrategy strategy = new(TransferSpeedType.Bodies, true, 0, 0, recalculateSpeedProbability, 0);

        const int iterations = 10_000;
        long peerWithSpeedPicked = 0;
        long peerWithoutSpeedPicked = 0;

        for (int i = 0; i < iterations; i++)
        {
            PeerInfo selectedPeer = strategy.Allocate(null, peers, nodeStatsManager, Build.A.BlockTree().TestObject)!;
            int selectedPeerIdx = peers.IndexOf(selectedPeer);
            if (peerSpeeds[selectedPeerIdx] is null)
            {
                peerWithoutSpeedPicked++;
            }
            else
            {
                peerWithSpeedPicked++;
            }
        }

        double noSpeedPeerChance = (double)peerWithoutSpeedPicked / (peerWithSpeedPicked + peerWithoutSpeedPicked);
        // The strategy's Random is unseeded; 0.04 is ~8 binomial standard deviations at p = 0.5, n = 10,000.
        double marginOfError = 0.04;
        Assert.That(noSpeedPeerChance, Is.InRange(chanceOfPickingPeerWithNoSpeed - marginOfError, chanceOfPickingPeerWithNoSpeed + marginOfError));
    }

    private static PeerInfo CreatePeerInfoWithSpeed(long? speed, INodeStatsManager nodeStatsManager)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        Node node = new(TestPublicKey, IPEndPoint.Parse("127.0.0.1"));
        syncPeer.Node.Returns(node);
        syncPeer.IsInitialized.Returns(true);

        PeerInfo pInfo = new(syncPeer);

        INodeStats nodeStats = Substitute.For<INodeStats>();
        nodeStats.GetAverageTransferSpeed(Arg.Any<TransferSpeedType>()).Returns(speed);
        nodeStatsManager.GetStatsOrNull(Arg.Is<Node>(n => ReferenceEquals(n, node))).Returns(nodeStats);
        return pInfo;
    }

    [Test]
    public async Task Unknown_speed_at_capacity_remains_eligible_for_real_allocation(
        [Values(TransferSpeedType.Headers, TransferSpeedType.Bodies)] TransferSpeedType speed, [Values] bool priority)
    {
        await using StatsCapacityContext context = new();
        BySpeedStrategy strategy = new(speed, priority, recalculateSpeedProbability: 0);

        PeerInfo? allocated = strategy.Allocate(null, [context.Peer], context.Stats, Build.A.BlockTree().TestObject);

        Assert.That(allocated, Is.SameAs(context.Peer));
    }

    [Test]
    public async Task Peer_report_includes_active_peer_when_stats_capacity_is_exhausted()
    {
        await using StatsCapacityContext context = new();
        SyncPeersReport report = new(Substitute.For<ISyncPeerPool>(), context.Stats, LimboLogs.Instance);

        string rendered = report.MakeReportForPeers([context.Peer], "test");

        Assert.That(rendered, Does.Contain("capacity-test-client").And.Contain("L/H/B/R/N/S"));
    }

    private sealed class StatsCapacityContext : IAsyncDisposable
    {
        public NodeStatsManager Stats { get; } = new(TimerFactory.Default, LimboLogs.Instance, 1);
        public PeerInfo Peer { get; }
        private readonly PeerPool _pool;
        private readonly PeerAddressPolicy _addressPolicy = new(new Nethermind.Core.Test.Modules.FixedIpResolver(new NetworkConfig { ExternalIp = "127.0.0.1" }),
            new LocalAddressSource(LimboLogs.Instance));

        public StatsCapacityContext()
        {
            _pool = new PeerPool(Substitute.For<INodeSource>(), Stats, Substitute.For<INetworkStorage>(),
                new NetworkConfig(), LimboLogs.Instance, Substitute.For<ITrustedNodesManager>(), TimeProvider.System,
                _addressPolicy);
            _pool.GetOrAdd(new Node(TestItem.PublicKeyA, "8.8.8.8", 30303, true));
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.Node.Returns(new Node(TestItem.PublicKeyB, "9.9.9.9", 30303));
            peer.IsInitialized.Returns(true);
            peer.ClientId.Returns("capacity-test-client");
            Peer = new PeerInfo(peer);
        }

        public async ValueTask DisposeAsync()
        {
            await _pool.StopAsync();
            Stats.Dispose();
            _addressPolicy.Dispose();
        }
    }
}

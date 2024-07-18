// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;
using PublicKey = Nethermind.Core.Crypto.PublicKey;

namespace Nethermind.Synchronization.Test.AllocationStrategies;

public class BySpeedStrategyTests
{
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
        selectedPeerIdx.Should().Be(expectedSelectedPeerIdx);
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
            selectedPeerIdx.Should().Be(peerWithKnownSpeed); // It picked the first peer with unknown speed
        }
        else
        {
            selectedPeerIdx.Should().BeLessThan(peerWithKnownSpeed); // It picked earlier peers which have known speed
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
        selectedPeerIdx.Should().BeGreaterThan(50);
    }

    [TestCase(10, 0, 0, 0)]
    [TestCase(10, 0, 1, 0)]
    [TestCase(10, 10, 1, 0.5)]
    [TestCase(10, 10, 0.5, 0.25)]
    [Retry(3)]
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

        long peerWithSpeedPicked = 0;
        long peerWithoutSpeedPicked = 0;

        for (int i = 0; i < 100; i++)
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
        double marginOfError = 0.1;
        noSpeedPeerChance.Should().BeInRange(chanceOfPickingPeerWithNoSpeed - marginOfError,
            chanceOfPickingPeerWithNoSpeed + marginOfError);
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
        nodeStatsManager.GetOrAdd(Arg.Is<Node>(n => ReferenceEquals(n, node))).Returns(nodeStats);
        return pInfo;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Overseer.Test.JsonRpc.Dto;
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
    private static PublicKey TestPublicKey = new(Bytes.FromHexString(
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
        long[] peerSpeeds = new long[]
        {
            100,
            90,
            120,
            50
        };

        INodeStatsManager nodeStatsManager = Substitute.For<INodeStatsManager>();

        List<PeerInfo> peers = new();
        for (int i = 0; i < peerSpeeds.Length; i++)
        {
            PeerInfo pInfo = CreatePeerInfoWithSpeed(peerSpeeds[i], nodeStatsManager);
            peers.Add(pInfo);
        }

        BySpeedStrategy strategy = new(TransferSpeedType.Bodies, true, minDiffPercentageForSpeedSwitch, minDiffSpeed);

        PeerInfo? currentPeer = null;
        if (currentPeerIdx != null) currentPeer = peers[currentPeerIdx.Value];

        PeerInfo? selectedPeer = strategy.Allocate(currentPeer, peers, nodeStatsManager, Build.A.BlockTree().TestObject);

        int selectedPeerIdx = peers.IndexOf(selectedPeer);
        selectedPeerIdx.Should().Be(expectedSelectedPeerIdx);
    }

    private static PeerInfo CreatePeerInfoWithSpeed(long speed, INodeStatsManager nodeStatsManager)
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

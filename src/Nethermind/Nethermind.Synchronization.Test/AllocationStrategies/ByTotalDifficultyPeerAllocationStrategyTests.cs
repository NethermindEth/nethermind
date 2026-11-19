// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Blocks;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.AllocationStrategies;

public class ByTotalDifficultyPeerAllocationStrategyTests
{
    // Local total difficulty 1000 and head difficulty 1, so a 16-block lead is a lead of (1 + 1) * 16 = 32.
    // Peer 0 leads by total difficulty, peer 1 is the fastest, and the average speed is the sum of the three divided by 3.
    [TestCase(1010UL, 70L, 100L, 100L, 1)] // under 16 blocks ahead and slower than the average: the fastest peer
    [TestCase(1010UL, 200L, 300L, 0L, 0)] // under 16 blocks ahead and faster than the average: the leader
    [TestCase(1032UL, 50L, 100L, 100L, 0)] // 16 blocks ahead: the leader, however slow
    public void Chooses_by_total_difficulty(ulong leaderTotalDifficulty, long leaderSpeed, long secondSpeed, long thirdSpeed, int expected)
    {
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo[] peers = [CreatePeer(stats, 0, leaderTotalDifficulty, 0, leaderSpeed), CreatePeer(stats, 1, 1005, 0, secondSpeed), CreatePeer(stats, 2, 1005, 0, thirdSpeed)];
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.BestSuggestedHeader.Returns(Build.A.BlockHeader.WithTotalDifficulty(1000).TestObject);
        blockTree.Head.Returns(Build.A.Block.WithDifficulty(1).TestObject);

        Assert.That(Allocate(peers, stats, blockTree), Is.EqualTo(expected));
    }

    // Without total difficulties the lead in blocks decides; the slow highest peer wins only with at least 16 blocks.
    [TestCase(16UL, 0)]
    [TestCase(15UL, 1)]
    public void Chooses_by_head_number_without_total_difficulty(ulong lead, int expected)
    {
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        PeerInfo[] peers = [CreatePeer(stats, 0, null, 1000 + lead, 10), CreatePeer(stats, 1, null, 1001, 100)];
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(1000).TestObject);

        Assert.That(Allocate(peers, stats, blockTree), Is.EqualTo(expected));
    }

    private static int Allocate(PeerInfo[] peers, INodeStatsManager stats, IBlockTree blockTree) =>
        Array.IndexOf(peers, new ByTotalDifficultyPeerAllocationStrategy(null).Allocate(null, peers, stats, blockTree));

    private static PeerInfo CreatePeer(INodeStatsManager stats, int index, ulong? totalDifficulty, ulong headNumber, long headersSpeed)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        Node node = new(TestItem.PublicKeys[index], "127.0.0.1", 30303);
        syncPeer.Node.Returns(node);
        syncPeer.IsInitialized.Returns(true);
        syncPeer.TotalDifficulty.Returns(totalDifficulty is null ? null : (UInt256?)totalDifficulty.Value);
        syncPeer.HeadNumber.Returns(headNumber);

        INodeStats nodeStats = Substitute.For<INodeStats>();
        nodeStats.GetAverageTransferSpeed(TransferSpeedType.Headers).Returns(headersSpeed);
        stats.GetOrAdd(node).Returns(nodeStats);
        return new PeerInfo(syncPeer);
    }
}

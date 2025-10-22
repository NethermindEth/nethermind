// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.StateSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync.SnapProtocolTests;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class StateSyncDispatcherTests
{
    private static IBlockTree _blockTree = null!;

    private ILogManager _logManager = null!;
#pragma warning disable NUnit1032
    private SyncPeerPool _pool = null!;
#pragma warning restore NUnit1032
    private StateSyncDispatcherTester _dispatcher = null!;

    private readonly PublicKey _publicKey = new("0x000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f");

    private const int ChainLength = 100;
    private static IBlockTree BlockTree => LazyInitializer.EnsureInitialized(ref _blockTree, static () => Build.A.BlockTree().OfChainLength(ChainLength).TestObject);

    [SetUp]
    public void Setup()
    {
        _logManager = LimboLogs.Instance;

        IBlockTree blockTree = CachedBlockTreeBuilder.OfLength((int)BlockTree.BestSuggestedHeader!.Number);
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        _pool = new SyncPeerPool(blockTree, new NodeStatsManager(timerFactory, LimboLogs.Instance), new TotalDifficultyBetterPeerStrategy(LimboLogs.Instance), LimboLogs.Instance, 25);
        _pool.Start();

        ISyncFeed<StateSyncBatch>? feed = Substitute.For<ISyncFeed<StateSyncBatch>>();
        IPoSSwitcher poSSwitcher = Substitute.For<IPoSSwitcher>();
        poSSwitcher.TransitionFinished.Returns(false);
        _dispatcher =
            new StateSyncDispatcherTester(feed, new StateSyncDownloader(_logManager), _pool, new StateSyncAllocationStrategyFactory(), _logManager);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _pool.DisposeAsync();
        await _dispatcher.DisposeAsync();
    }

    [Test]
    public async Task Eth66Peer_RunGetNodeData()
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
        peer.ProtocolVersion.Returns((byte)66);
        peer.IsInitialized.Returns(true);
        peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
        peer.HeadNumber.Returns(ChainLength - 1);

        using Nethermind.Core.Collections.ArrayPoolList<byte[]> response = new(1);
        response.Add(new byte[] { 1, 2, 3 });
        peer.GetNodeData(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(response);

        _pool.AddPeer(peer);

        using StateSyncBatch batch = new(
            Keccak.OfAnEmptyString,
            NodeDataType.State,
            new[] { new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State) });

        await _dispatcher.ExecuteDispatch(batch, 1);

        using var _ = await peer.ReceivedWithAnyArgs(1).GetNodeData(default!, default);
    }

    [Test]
    public async Task GroupMultipleStorageSlotsByAccount()
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
        peer.ProtocolVersion.Returns((byte)67);
        peer.IsInitialized.Returns(true);
        peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
        peer.HeadNumber.Returns(ChainLength - 1);
        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();

        using Nethermind.Core.Collections.ArrayPoolList<byte[]> snapResponse = new(6);
        for (int i = 0; i < 6; i++)
        {
            snapResponse.Add(new byte[] { (byte)i });
        }
        snapPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(snapResponse);

        peer.TryGetSatelliteProtocol("snap", out Arg.Any<ISnapSyncPeer>()).Returns(
            x =>
            {
                x[1] = snapPeer;
                return true;
            });
        _pool.AddPeer(peer);

        StateSyncItem item01 = new(Keccak.EmptyTreeHash, null, TreePath.FromNibble([3]), NodeDataType.State);
        StateSyncItem item02 = new(Keccak.EmptyTreeHash, TestItem.KeccakA, TreePath.FromNibble([2]), NodeDataType.State);
        StateSyncItem item03 = new(Keccak.EmptyTreeHash, null, TreePath.FromNibble([1]), NodeDataType.State);
        StateSyncItem item04 = new(Keccak.EmptyTreeHash, TestItem.KeccakB, TreePath.FromNibble([21]), NodeDataType.State);
        StateSyncItem item05 = new(Keccak.EmptyTreeHash, TestItem.KeccakA, TreePath.FromNibble([1]), NodeDataType.State);
        StateSyncItem item06 = new(Keccak.EmptyTreeHash, TestItem.KeccakB, TreePath.FromNibble([22]), NodeDataType.State);

        using StateSyncBatch batch = new(
            Keccak.OfAnEmptyString,
            NodeDataType.State,
            new[] { item01, item02, item03, item04, item05, item06 });

        await _dispatcher.ExecuteDispatch(batch, 1);

        batch.RequestedNodes.Should().NotBeNull();
        batch.RequestedNodes!.Count.Should().Be(6);
        batch.RequestedNodes[0].Should().Be(item01);
        batch.RequestedNodes[1].Should().Be(item03);
        batch.RequestedNodes[2].Should().Be(item02);
        batch.RequestedNodes[3].Should().Be(item05);
        batch.RequestedNodes[4].Should().Be(item04);
        batch.RequestedNodes[5].Should().Be(item06);
    }

    [Test]
    public async Task NodeDataPeer_FallbackToSnapWhenEmpty()
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
        peer.ProtocolVersion.Returns((byte)67);
        peer.IsInitialized.Returns(true);
        peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
        peer.HeadNumber.Returns(ChainLength - 1);

        INodeDataPeer nodeDataHandler = Substitute.For<INodeDataPeer>();
        using Nethermind.Core.Collections.ArrayPoolList<byte[]> emptyResponse = new(0);
        nodeDataHandler.GetNodeData(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(emptyResponse);

        peer.TryGetSatelliteProtocol("nodedata", out Arg.Any<INodeDataPeer>()).Returns(
            x =>
            {
                x[1] = nodeDataHandler;
                return true;
            });

        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
        using Nethermind.Core.Collections.ArrayPoolList<byte[]> snapResponse = new(1);
        snapResponse.Add(new byte[] { 1, 2, 3 });
        snapPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(snapResponse);

        peer.TryGetSatelliteProtocol("snap", out Arg.Any<ISnapSyncPeer>()).Returns(
            x =>
            {
                x[1] = snapPeer;
                return true;
            });

        _pool.AddPeer(peer);

        using StateSyncBatch batch = new(
            Keccak.OfAnEmptyString,
            NodeDataType.State,
            new[] { new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State) });

        await _dispatcher.ExecuteDispatch(batch, 1);

        await nodeDataHandler.Received(1).GetNodeData(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>());
        await snapPeer.Received(1).GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>());
        batch.Responses.Should().NotBeNull();
        batch.Responses!.Count.Should().Be(1);
    }

    [Test]
    public async Task Eth66Peer_FallbackToSnapWhenEmpty()
    {
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.Node.Returns(new Stats.Model.Node(_publicKey, new IPEndPoint(IPAddress.Broadcast, 30303)));
        peer.ProtocolVersion.Returns((byte)66);
        peer.IsInitialized.Returns(true);
        peer.TotalDifficulty.Returns(new Int256.UInt256(1_000_000_000));
        peer.HeadNumber.Returns(ChainLength - 1);

        using Nethermind.Core.Collections.ArrayPoolList<byte[]> emptyResponse = new(0);
        peer.GetNodeData(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>())
            .Returns(emptyResponse);

        ISnapSyncPeer snapPeer = Substitute.For<ISnapSyncPeer>();
        using Nethermind.Core.Collections.ArrayPoolList<byte[]> snapResponse = new(1);
        snapResponse.Add(new byte[] { 1, 2, 3 });
        snapPeer.GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>())
            .Returns(snapResponse);

        peer.TryGetSatelliteProtocol("snap", out Arg.Any<ISnapSyncPeer>()).Returns(
            x =>
            {
                x[1] = snapPeer;
                return true;
            });

        _pool.AddPeer(peer);

        using StateSyncBatch batch = new(
            Keccak.OfAnEmptyString,
            NodeDataType.State,
            new[] { new StateSyncItem(Keccak.EmptyTreeHash, null, TreePath.Empty, NodeDataType.State) });

        await _dispatcher.ExecuteDispatch(batch, 1);

        using var _ = await peer.Received(1).GetNodeData(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<CancellationToken>());
        await snapPeer.Received(1).GetTrieNodes(Arg.Any<GetTrieNodesRequest>(), Arg.Any<CancellationToken>());
        batch.Responses.Should().NotBeNull();
        batch.Responses!.Count.Should().Be(1);
    }
}
